using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LinqKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NNostr.Client;
using Relay.Data;

namespace Relay
{
    public class NostrEventService
    {
        private ConcurrentDictionary<string, RelayNostrEvent[]> CachedFilterResults =
            new();

        private readonly IDbContextFactory<RelayDbContext> _dbContextFactory;
        private readonly ILogger<NostrEventService> _logger;
        private readonly IOptionsMonitor<RelayOptions> _options;
        public event EventHandler<NostrEventsMatched>? EventsMatched;
        public event EventHandler<RelayNostrEvent[]>? NewEvents;

        private ConcurrentDictionary<string, NostrSubscriptionFilter> ActiveFilters { get; set; } =
            new();

        public NostrEventService(IDbContextFactory<RelayDbContext> dbContextFactory, ILogger<NostrEventService> logger,
            IOptionsMonitor<RelayOptions> options)
        {
            _dbContextFactory = dbContextFactory;
            _logger = logger;
            _options = options;
        }

        private long ComputeCost(RelayNostrEvent evt, out bool isToAdmin)
        {
            var adminPubKey = _options.CurrentValue.AdminPublicKey;
            isToAdmin = false;
            if (evt.PublicKey == adminPubKey)
            {
                isToAdmin = true;
                return 0;
            }

            if (evt.Kind == 4 && evt.Tags.Any(tag =>
                    tag.TagIdentifier == "p" &&
                    tag.Data.First().Equals(adminPubKey, StringComparison.InvariantCultureIgnoreCase)))
            {
                isToAdmin = true;
                return 0;
            }

            if (!_options.CurrentValue.EventCostPerByte)
            {
                return _options.CurrentValue.EventCost;
            }

            return _options.CurrentValue.EventCost * Encoding.UTF8.GetByteCount(evt.ToJson<RelayNostrEvent,RelayNostrEventTag>(false));
        }

        public async Task<List<(string eventId, bool success, string reason)>> AddEvent(RelayNostrEvent[] evt)
        {
            var eventResults = new List<(string eventId, bool success, string reason)>();
            var evtIds = evt.Select(e => e.Id).ToArray();
            await using var context = await _dbContextFactory.CreateDbContextAsync();
            var alreadyPresentEventIds =
                await context.Events.Where(e => evtIds.Contains(e.Id)).Select(e => e.Id).ToArrayAsync();
            evt = evt.Where(e => !alreadyPresentEventIds.Contains(e.Id)).ToArray();
            eventResults.AddRange(alreadyPresentEventIds.Select(s => (s, true, "duplicate: Event has been processed before")));
            var invalidnip22 = evt.Where(e =>
                !((_options.CurrentValue.Nip22BackwardLimit is null ||
                  (DateTimeOffset.UtcNow - e.CreatedAt) <= _options.CurrentValue.Nip22BackwardLimit) &&
                 (_options.CurrentValue.Nip22ForwardLimit is null ||
                  (e.CreatedAt - DateTimeOffset.UtcNow) <= _options.CurrentValue.Nip22ForwardLimit)));
            
            eventResults.AddRange(invalidnip22.Select(s => (s.Id, false, "invalid: event creation date is too far off from the current time. Is your system clock in sync?")));
            evt = evt.Except(invalidnip22).ToArray();

            if (_options.CurrentValue.EventCost > 0 || _options.CurrentValue.PubKeyCost > 0)
            {
                var eventsGroupedByAuthor = evt.GroupBy(e => e.PublicKey);
                var eventsGroupedByAuthorItems = eventsGroupedByAuthor as IGrouping<string, RelayNostrEvent>[] ??
                                                 eventsGroupedByAuthor.ToArray();
                var authors = eventsGroupedByAuthorItems.Select(events => events.Key).ToHashSet();
                var balanceLookup =
                    (await context.Balances.Where(balance => authors.Contains(balance.PublicKey)).ToListAsync())
                    .ToDictionary(balance => balance.PublicKey);

                var notvalid = new List<RelayNostrEvent>();
                foreach (var eventsGroupedByAuthorItem in eventsGroupedByAuthorItems)
                {
                    balanceLookup.TryGetValue(eventsGroupedByAuthorItem.Key, out var authorBalance);
                    authorBalance ??= new Balance()
                    {
                        CurrentBalance = _options.CurrentValue.PubKeyCost * -1,
                    };
                    // if (authorBalance.CurrentBalance < 0 ||
                    //     (authorBalance.CurrentBalance == 0 && _options.CurrentValue.EventCost > 0))
                    // {
                    //     notvalid.AddRange(eventsGroupedByAuthorItem);
                    // }
                    foreach (var eventsGroupedByAuthorItemEvt in eventsGroupedByAuthorItem)
                    {
                        var cost = ComputeCost(eventsGroupedByAuthorItemEvt, out var isToAdmin);
                        if (!isToAdmin && (authorBalance.CurrentBalance - cost) < 0)
                        {
                            notvalid.Add(eventsGroupedByAuthorItemEvt);
                        }
                        else if (cost != 0)
                        {
                            authorBalance.CurrentBalance -= _options.CurrentValue.EventCost;
                            await context.BalanceTransactions.AddAsync(new BalanceTransaction()
                            {
                                BalanceId = eventsGroupedByAuthorItem.Key,
                                Timestamp = eventsGroupedByAuthorItemEvt.CreatedAt ?? DateTimeOffset.UtcNow,
                                Value = cost * -1,
                                EventId = eventsGroupedByAuthorItemEvt.Id
                            });
                        }
                    }
                }
                eventResults.AddRange(notvalid.Select(s => (s.Id, false, "invalid: this relay has a cost associated with this event and you did not have sufficient balance")));

                evt = evt.Where(e => !notvalid.Contains(e)).ToArray();
            }

            _logger.LogInformation($"Processing/Saving {evt.Length} new events");

            var removedEvents = new List<string>();
            if (_options.CurrentValue.EnableNip09)
            {
                var deletionEvents = evt.Where(e => e.Kind == 5).ToArray();
                if (deletionEvents.Any())
                {
                    var eventsToDeleteByPubKey = deletionEvents.Select(evt2 => (evt2.PublicKey, evt2.Tags.FindAll(tag =>
                                tag.TagIdentifier.Equals("e", StringComparison.InvariantCultureIgnoreCase))
                            .Select(tag => tag.Data.First())))
                        .GroupBy(tuple => tuple.PublicKey)
                        .ToDictionary(tuples => tuples.Key, tuples => tuples.SelectMany(tuple => tuple.Item2));
                    foreach (var eventsToDeleteByPubKeyItem in eventsToDeleteByPubKey)
                    {
                        await context.Events.Where(evt2 =>
                                evt2.PublicKey.Equals(eventsToDeleteByPubKeyItem.Key) &&
                                !evt2.Deleted && eventsToDeleteByPubKeyItem.Value.Contains(evt2.Id))
                            .ForEachAsync(evt2 =>
                            {
                                // clients still receive a copy of the original note so we shouldnt remove from filter results
                                // removedEvents.Add(evt2);  
                                evt2.Deleted = true;
                            });
                    }
                }
            }

            var evtsToSave = evt;
            if (_options.CurrentValue.EnableNip16)
            {
                var replaceableEvents = evt.Where(e => e.Kind is >= 10000 and < 20000).ToArray();
                var replacedEvents = new List<RelayNostrEvent>();
                foreach (var eventsToReplace in replaceableEvents)
                {
                    replacedEvents.AddRange(context.Events.Where(evt2 =>
                        evt2.PublicKey.Equals(eventsToReplace.Id) && eventsToReplace.Kind == evt2.Kind &&
                        evt2.CreatedAt < eventsToReplace.CreatedAt));
                }

                context.Events.RemoveRange(replacedEvents);
                removedEvents.AddRange(replacedEvents.Select(e => e.Id));
                //ephemeral events
                evtsToSave = evt.Where(e => e.Kind is not (>= 20000 and < 30000)).ToArray();
            }
            if (_options.CurrentValue.EnableNip33)
            {
                var replaceableEvents = evt.Where(e => e.Kind is >= 30000 and < 40000).ToArray();
                var replacedEvents = new List<RelayNostrEvent>();
                foreach (var eventsToReplace in replaceableEvents)
                {
                    var dValue = eventsToReplace.GetTaggedData<RelayNostrEvent, RelayNostrEventTag>("d").FirstOrDefault() ?? "";

                    var caluse = PredicateBuilder.New<RelayNostrEvent>()
                        .And(@event => @event.PublicKey == eventsToReplace.Id)
                        .And(@event => @event.Kind == eventsToReplace.Kind)
                        .And(@event => @event.CreatedAt < eventsToReplace.CreatedAt);
                        // .And(@event =>
                        //     (@event.GetTaggedData<RelayNostrEvent, RelayNostrEventTag>("d").FirstOrDefault() ??
                        //     "") == dValue);

                        
                    var toreplace =  await context.Events.Where(caluse).ToListAsync();
                    toreplace = toreplace.Where(@event => dValue == (
                        @event.GetTaggedData<RelayNostrEvent, RelayNostrEventTag>("d").FirstOrDefault() ?? "")).ToList();
                    replacedEvents.AddRange(toreplace);
                    // replacedEvents.AddRange(context.Events.Where(evt2 =>
                    //     evt2.PublicKey.Equals(eventsToReplace.Id) && 
                    //     eventsToReplace.Kind == evt2.Kind &&
                    //     dValue== (evt2.GetTaggedData<RelayNostrEvent,RelayNostrEventTag>("d").FirstOrDefault()??"") &&
                    //     evt2.CreatedAt < eventsToReplace.CreatedAt));
                }

                context.Events.RemoveRange(replacedEvents);
                removedEvents.AddRange(replacedEvents.Select(e => e.Id));
            }


            foreach (var nostrSubscriptionFilter in ActiveFilters)
            {
                var matched = evt.Filter<RelayNostrEvent,RelayNostrEventTag>( nostrSubscriptionFilter.Value).ToArray();
                if (!matched.Any()) continue;

                var matchedList = matched.ToArray();
                _logger.LogInformation(
                    $"Updated filter {nostrSubscriptionFilter.Key} with {matchedList.Length} new events");
                if (CachedFilterResults.TryGetValue(nostrSubscriptionFilter.Key, out var currentFilterValues))
                {
                    var updatedResult = currentFilterValues.Concat(matchedList)
                        .Where(e => !removedEvents.Contains(e.Id)).FilterByLimit<RelayNostrEvent, RelayNostrEventTag>(nostrSubscriptionFilter.Value.Limit)
                        .ToArray();
                    CachedFilterResults[nostrSubscriptionFilter.Key] = updatedResult;
                }
                else
                {
                    CachedFilterResults.TryAdd(nostrSubscriptionFilter.Key,
                        matchedList.Where(e => !removedEvents.Contains(e.Id))
                            .FilterByLimit<RelayNostrEvent, RelayNostrEventTag>(nostrSubscriptionFilter.Value.Limit).ToArray());
                }
                InvokeMatched(new NostrEventsMatched()
                {
                    Events = matchedList,
                    FilterId = nostrSubscriptionFilter.Key
                });
            }
            eventResults.AddRange(evtsToSave.Select(@event => (@event.Id, true, "")));
            await context.Events.AddRangeAsync(
                evtsToSave.Select(@event => 
                    JsonSerializer.Deserialize<RelayNostrEvent>( JsonSerializer.Serialize(@event)))!);
            await context.SaveChangesAsync();
            NewEvents?.Invoke(this, evt);
            return eventResults;
        }

        public async Task<NostrEventsMatched> AddFilter(NostrSubscriptionFilter filter)
        {
            var id = JsonSerializer.Serialize(filter).ComputeSha256Hash().AsSpan().ToHex();
            ActiveFilters.TryAdd(id, filter);
            return new NostrEventsMatched()
            {
                Events = await CachedFilterResults.GetOrAddAsync(id, GetFromDB),
                FilterId = id,
                InitialRequest = true
            };
        }

        public void InvokeMatched(NostrEventsMatched eventsMatched)
        {
            EventsMatched?.Invoke(this, eventsMatched);
        }

        public async Task<RelayNostrEvent[]> FetchData(params NostrSubscriptionFilter[] filter)
        {
            var result = new List<RelayNostrEvent>();
            foreach (var nostrSubscriptionFilter in filter)
            {
                var id = JsonSerializer.Serialize(nostrSubscriptionFilter).ComputeSha256Hash().AsSpan().ToHex();
                result.AddRange(await CachedFilterResults.GetOrAddAsync(id, s => GetFromDB(nostrSubscriptionFilter)));
            }

            return result.Distinct().ToArray();
        }

        private async Task<RelayNostrEvent[]> GetFromDB(string filterId)
        {
            if (ActiveFilters.TryGetValue(filterId, out var filter))
            {
                return await GetFromDB(filter);
            }

            throw new ArgumentOutOfRangeException(nameof(filterId), "Filter is not active");
        }

        private async Task<RelayNostrEvent[]> GetFromDB(NostrSubscriptionFilter filter)
        {
            await using var context = await _dbContextFactory.CreateDbContextAsync();
            var result =  await context.Events
                .Include(e => e.Tags)
                .Where(e => !e.Deleted)
                .Filter<RelayNostrEvent,RelayNostrEventTag>(filter)
                .ToArrayAsync();

            return result.OrderBy(e => e.CreatedAt).ToArray();
        }

        public void RemoveFilter(string removedFilter)
        {
            if (!ActiveFilters.Remove(removedFilter, out _)) return;
            _logger.LogInformation($"Removing filter: {removedFilter}");
            CachedFilterResults.Remove(removedFilter, out _);
        }
    }
}
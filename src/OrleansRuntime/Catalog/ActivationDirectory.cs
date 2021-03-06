/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;


namespace Orleans.Runtime
{
    internal class ActivationDirectory : IEnumerable<KeyValuePair<ActivationId, ActivationData>>
    {
        private static readonly TraceLogger logger = TraceLogger.GetLogger("ActivationDirectory", TraceLogger.LoggerType.Runtime);

        private readonly ConcurrentDictionary<ActivationId, ActivationData> activations;                // Activation data (app grains) only.
        private readonly ConcurrentDictionary<ActivationId, SystemTarget> systemTargets;                // SystemTarget only.
        private readonly ConcurrentDictionary<GrainId, List<ActivationData>> grainToActivationsMap;     // Activation data (app grains) only.
        private readonly ConcurrentDictionary<string, CounterStatistic> grainCounts;                    // simple statistics type->count
        private readonly ConcurrentDictionary<string, CounterStatistic> systemTargetCounts;             // simple statistics systemTargetTypeName->count

        internal ActivationDirectory()
        {
            activations = new ConcurrentDictionary<ActivationId, ActivationData>();
            systemTargets = new ConcurrentDictionary<ActivationId, SystemTarget>();
            grainToActivationsMap = new ConcurrentDictionary<GrainId, List<ActivationData>>();
            grainCounts = new ConcurrentDictionary<string, CounterStatistic>();
            systemTargetCounts = new ConcurrentDictionary<string, CounterStatistic>();
        }

        public int Count
        {
            get { return activations.Count; }
        }

        public IEnumerable<SystemTarget> AllSystemTargets()
        {
            return systemTargets.Values;
        }

        public ActivationData FindTarget(ActivationId key)
        {
            ActivationData target;
            return activations.TryGetValue(key, out target) ? target : null;
        }

        public SystemTarget FindSystemTarget(ActivationId key)
        {
            SystemTarget target;
            return systemTargets.TryGetValue(key, out target) ? target : null;
        }

        internal void IncrementGrainCounter(string grainTypeName)
        {
            if (logger.IsVerbose2) logger.Verbose2("Increment Grain Counter {0}", grainTypeName);
            CounterStatistic ctr = FindGrainCounter(grainTypeName);
            ctr.Increment();
        }
        internal void DecrementGrainCounter(string grainTypeName)
        {
            if (logger.IsVerbose2) logger.Verbose2("Decrement Grain Counter {0}", grainTypeName);
            CounterStatistic ctr = FindGrainCounter(grainTypeName);
            ctr.DecrementBy(1);
        }

        private CounterStatistic FindGrainCounter(string grainTypeName)
        {
            CounterStatistic ctr;
            if (grainCounts.TryGetValue(grainTypeName, out ctr)) return ctr;

            var counterName = new StatisticName(StatisticNames.GRAIN_COUNTS_PER_GRAIN, grainTypeName);
            ctr = grainCounts[grainTypeName] = CounterStatistic.FindOrCreate(counterName, false);
            return ctr;
        }

        private CounterStatistic FindSystemTargetCounter(string systemTargetTypeName)
        {
            CounterStatistic ctr;
            if (systemTargetCounts.TryGetValue(systemTargetTypeName, out ctr)) return ctr;

            var counterName = new StatisticName(StatisticNames.SYSTEM_TARGET_COUNTS, systemTargetTypeName);
            ctr = systemTargetCounts[systemTargetTypeName] = CounterStatistic.FindOrCreate(counterName, false);
            return ctr;
        }

        public void RecordNewTarget(ActivationData target)
        {
            if (!activations.TryAdd(target.ActivationId, target))
                return;
            grainToActivationsMap.AddOrUpdate(target.Grain,
                g => new List<ActivationData> { target },
                (g, list) => { lock (list) { list.Add(target); } return list; });
        }

        public void RecordNewSystemTarget(SystemTarget target)
        {
            systemTargets.TryAdd(target.ActivationId, target);
            if (!Constants.IsSingletonSystemTarget(target.GrainId))
            {
                FindSystemTargetCounter(Constants.SystemTargetName(target.GrainId)).Increment();
            }
        }

        public void RemoveSystemTarget(SystemTarget target)
        {
            SystemTarget ignore;
            systemTargets.TryRemove(target.ActivationId, out ignore);
            if (!Constants.IsSingletonSystemTarget(target.GrainId))
            {
                FindSystemTargetCounter(Constants.SystemTargetName(target.GrainId)).DecrementBy(1);
            }
        }

        public void RemoveTarget(ActivationData target)
        {
            ActivationData ignore;
            if (!activations.TryRemove(target.ActivationId, out ignore))
                return;
            List<ActivationData> list;
            if (grainToActivationsMap.TryGetValue(target.Grain, out list))
            {
                lock (list)
                {
                    list.Remove(target);
                    if (list.Count == 0)
                    {
                        List<ActivationData> list2; // == list
                        if (grainToActivationsMap.TryRemove(target.Grain, out list2))
                        {
                            lock (list2)
                            {
                                if (list2.Count > 0)
                                {
                                    grainToActivationsMap.AddOrUpdate(target.Grain,
                                        g => list2,
                                        (g, list3) => { lock (list3) { list3.AddRange(list2); } return list3; });
                                }
                            }
                        }
                    }
                }
            }
        }

        // Returns null if no activations exist for this grain ID, rather than an empty list
        public List<ActivationData> FindTargets(GrainId key)
        {
            List<ActivationData> tmp;
            if (grainToActivationsMap.TryGetValue(key, out tmp))
            {
                lock (tmp)
                {
                    return tmp.ToList();
                }
            }
            return null;
        }

        public IEnumerable<KeyValuePair<string, long>> GetSimpleGrainStatistics()
        {
            return grainCounts
                .Select(s => new KeyValuePair<string, long>(s.Key, s.Value.GetCurrentValue()))
                .Where(p => p.Value > 0);
        }

        public void PrintActivationDirectory()
        {
            if (logger.IsInfo)
            {
                string stats = Utils.EnumerableToString(activations.Values.OrderBy(act => act.Name), act => string.Format("++{0}", act.DumpStatus()), "\r\n");
                if (stats.Length > 0)
                {
                    logger.LogWithoutBulkingAndTruncating(Logger.Severity.Info, ErrorCode.Catalog_ActivationDirectory_Statistics, String.Format("ActivationDirectory.PrintActivationDirectory(): Size = {0}, Directory:\n{1}",
                        activations.Count, stats));
                }
            }
        }

        #region Implementation of IEnumerable

        public IEnumerator<KeyValuePair<ActivationId, ActivationData>> GetEnumerator()
        {
            return activations.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        #endregion
    }
}

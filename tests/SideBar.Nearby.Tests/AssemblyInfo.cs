// NearbyLog.Log is process-global and some tests install a sink: one collection for the whole assembly runs it serially.
[assembly: Xunit.CollectionBehavior(Xunit.CollectionBehavior.CollectionPerAssembly)]

        // foreach (var metric in toCollapseMetrics)
        // {
        //     var dailyKey = (metric.UserId, metric.Day);
        //     var monthlyKey = (metric.UserId, new DateOnly(metric.Day.Year, metric.Day.Month, 1));

        //     if (metric.IsJailbreakDetected)
        //     {
        //         _dailyJailbreakCount.AddOrUpdate(dailyKey, 1L, static (_, count) => count + 1);
        //         _monthlyJailbreakCount.AddOrUpdate(monthlyKey, 1L, static (_, count) => count + 1);
        //     }

        //     if (metric.IsContentFiltered)
        //     {
        //         _dailyContentFilteredCount.AddOrUpdate(dailyKey, 1L, static (_, count) => count + 1);
        //         _monthlyContentFilteredCount.AddOrUpdate(monthlyKey, 1L, static (_, count) => count + 1);
        //     }

        //     if (metric.StatusCode.HasValue && metric.LatencyMs.HasValue)
        //     {
        //         var queue = GetUserSampleQueue(metric.UserId);
        //         queue.Enqueue(new RequestOutcomeSample(
        //             metric.TimestampUtc,
        //             metric.StatusCode.Value,
        //             Math.Max(0d, metric.LatencyMs.Value)));

        //         while (queue.Count > MaxRequestSamplesPerUser)
        //         {
        //             queue.TryDequeue(out _);
        //         }
        //     }
        // }


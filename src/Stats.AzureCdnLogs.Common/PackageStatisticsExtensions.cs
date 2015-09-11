// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Stats.AzureCdnLogs.Common
{
    public static class PackageStatisticsExtensions
    {
        public static IEnumerable<string> GetProjectGuidsAsEnumerable(this PackageStatistics current)
        {
            if (string.IsNullOrEmpty(current.ProjectGuids) || current.ProjectGuids.Length == 1)
            {
                return Enumerable.Empty<string>();
            }

            return current.ProjectGuids.Split(new[] {";"}, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
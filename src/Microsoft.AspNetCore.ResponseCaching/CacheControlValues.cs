using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Microsoft.AspNetCore.ResponseCaching.Internal
{
    internal class CacheControlValues
    {
        public const string MaxAgeString = "max-age";
        public const string MaxStaleString = "max-stale";
        public const string MinFreshString = "min-fresh";
        public const string MustRevalidateString = "must-revalidate";
        public const string NoCacheString = "no-cache";
        public const string NoStoreString = "no-store";
        public const string NoTransformString = "no-transform";
        public const string OnlyIfCachedString = "only-if-cached";
        public const string PrivateString = "private";
        public const string ProxyRevalidateString = "proxy-revalidate";
        public const string PublicString = "public";
        public const string SharedMaxAgeString = "s-maxage";
    }
}

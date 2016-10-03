// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.ResponseCaching.Internal
{
    public class ResponseCachingPolicyProvider : IResponseCachingPolicyProvider
    {
        private static readonly CacheControlHeaderValue EmptyCacheControl = new CacheControlHeaderValue();

        public virtual bool IsRequestCacheable(ResponseCachingContext context)
        {
            // Verify the method
            var request = context.HttpContext.Request;
            if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
            {
                context.Logger.LogRequestMethodNotCacheable(request.Method);
                return false;
            }

            // Verify existence of authorization headers
            if (!StringValues.IsNullOrEmpty(request.Headers[HeaderNames.Authorization]))
            {
                context.Logger.LogRequestWithAuthorizationNotCacheable();
                return false;
            }

            // Verify request cache-control parameters
            if (!StringValues.IsNullOrEmpty(request.Headers[HeaderNames.CacheControl]))
            {
                foreach (var header in request.Headers[HeaderNames.CacheControl])
                {
                    if (header.Equals("no-cache"))
                    {
                        context.Logger.LogRequestWithNoCacheNotCacheable();
                        return false;
                    }
                }
            }
            else
            {
                // Support for legacy HTTP 1.0 cache directive
                var pragmaHeaderValues = request.Headers[HeaderNames.Pragma];
                foreach (var directive in pragmaHeaderValues)
                {
                    if (string.Equals("no-cache", directive, StringComparison.OrdinalIgnoreCase))
                    {
                        context.Logger.LogRequestWithPragmaNoCacheNotCacheable();
                        return false;
                    }
                }
            }

            return true;
        }

        public virtual bool IsResponseCacheable(ResponseCachingContext context)
        {
            // Only cache pages explicitly marked with public
            if (!context.ResponseCacheControlHeaderValue.Public)
            {
                context.Logger.LogResponseWithoutPublicNotCacheable();
                return false;
            }

            // Check no-store
            foreach (var header in context.HttpContext.Request.Headers[HeaderNames.CacheControl])
            {
                if (header.Equals("no-store"))
                {
                    context.Logger.LogResponseWithNoStoreNotCacheable();
                    return false;
                }
            }

            if (context.ResponseCacheControlHeaderValue.NoStore)
            {
                context.Logger.LogResponseWithNoStoreNotCacheable();
                return false;
            }

            // Check no-cache
            if (context.ResponseCacheControlHeaderValue.NoCache)
            {
                context.Logger.LogResponseWithNoCacheNotCacheable();
                return false;
            }

            var response = context.HttpContext.Response;

            // Do not cache responses with Set-Cookie headers
            if (!StringValues.IsNullOrEmpty(response.Headers[HeaderNames.SetCookie]))
            {
                context.Logger.LogResponseWithSetCookieNotCacheable();
                return false;
            }

            // Do not cache responses varying by *
            var varyHeader = response.Headers[HeaderNames.Vary];
            if (varyHeader.Count == 1 && string.Equals(varyHeader, "*", StringComparison.OrdinalIgnoreCase))
            {
                context.Logger.LogResponseWithVaryStarNotCacheable();
                return false;
            }

            // Check private
            if (context.ResponseCacheControlHeaderValue.Private)
            {
                context.Logger.LogResponseWithPrivateNotCacheable();
                return false;
            }

            // Check response code
            if (response.StatusCode != StatusCodes.Status200OK)
            {
                context.Logger.LogResponseWithUnsuccessfulStatusCodeNotCacheable(response.StatusCode);
                return false;
            }

            // Check response freshness
            if (!context.ResponseDate.HasValue)
            {
                if (!context.ResponseCacheControlHeaderValue.SharedMaxAge.HasValue &&
                    !context.ResponseCacheControlHeaderValue.MaxAge.HasValue &&
                    context.ResponseTime.Value >= context.ResponseExpires)
                {
                    context.Logger.LogExpirationExpiresExceeded(context.ResponseTime.Value, context.ResponseExpires.Value);
                    return false;
                }
            }
            else
            {
                var age = context.ResponseTime.Value - context.ResponseDate.Value;

                // Validate shared max age
                var sharedMaxAge = context.ResponseCacheControlHeaderValue.SharedMaxAge;
                if (age >= sharedMaxAge)
                {
                    context.Logger.LogExpirationSharedMaxAgeExceeded(age, sharedMaxAge.Value);
                    return false;
                }
                else if (!sharedMaxAge.HasValue)
                {
                    // Validate max age
                    var maxAge = context.ResponseCacheControlHeaderValue.MaxAge;
                    if (age >= maxAge)
                    {
                        context.Logger.LogExpirationMaxAgeExceeded(age, maxAge.Value);
                        return false;
                    }
                    else if (!maxAge.HasValue)
                    {
                        // Validate expiration
                        if (context.ResponseTime.Value >= context.ResponseExpires)
                        {
                            context.Logger.LogExpirationExpiresExceeded(context.ResponseTime.Value, context.ResponseExpires.Value);
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        public virtual bool IsCachedEntryFresh(ResponseCachingContext context)
        {
            var age = context.CachedEntryAge.Value;
            var cachedControlHeaders = context.CachedResponseHeaders.CacheControl ?? EmptyCacheControl;
            var requestCacheControlHeaders = context.HttpContext.Request.Headers[HeaderNames.CacheControl];

            // Add min-fresh requirements
            foreach (var header in requestCacheControlHeaders)
            {
                var index = header.IndexOf("min-fresh", StringComparison.OrdinalIgnoreCase);
                if (index != -1)
                {
                    index += 9;
                    int seconds;
                    if (TryParseValue(index, header, out seconds))
                    {
                        var minFresh = new TimeSpan(0, 0, seconds);
                        age += minFresh;
                        context.Logger.LogExpirationMinFreshAdded(minFresh);
                    }
                    break;
                }
            }

            // Validate shared max age, this overrides any max age settings for shared caches
            var sharedMaxAge = cachedControlHeaders.SharedMaxAge;
            if (age >= sharedMaxAge)
            {
                // shared max age implies must revalidate
                context.Logger.LogExpirationSharedMaxAgeExceeded(age, sharedMaxAge.Value);
                return false;
            }
            else if (!sharedMaxAge.HasValue)
            {
                var cachedMaxAge = cachedControlHeaders.MaxAge;
                TimeSpan? requestMaxAge = null;
                foreach (var header in requestCacheControlHeaders)
                {
                    var index = header.IndexOf("max-age");
                    if (index != -1)
                    {
                        index += 7;
                        int seconds;
                        if (TryParseValue(index, header, out seconds))
                        {
                            requestMaxAge = new TimeSpan(0, 0, seconds);
                        }
                        break;
                    }
                }

                var lowestMaxAge = cachedMaxAge < requestMaxAge ? cachedMaxAge : requestMaxAge ?? cachedMaxAge;
                // Validate max age
                if (age >= lowestMaxAge)
                {
                    // Must revalidate
                    if (cachedControlHeaders.MustRevalidate)
                    {
                        context.Logger.LogExpirationMustRevalidate(age, lowestMaxAge.Value);
                        return false;
                    }

                    // Request allows stale values
                    TimeSpan? requestMaxStale = null;
                    foreach (var header in requestCacheControlHeaders)
                    {
                        var index = header.IndexOf("max-stale");
                        if (index != -1)
                        {
                            index += 9;
                            int seconds;
                            if (TryParseValue(index, header, out seconds))
                            {
                                requestMaxStale = new TimeSpan(0, 0, seconds);
                            }
                            break;
                        }
                    }

                    if (requestMaxStale.HasValue && age - lowestMaxAge < requestMaxStale)
                    {
                        context.Logger.LogExpirationMaxStaleSatisfied(age, lowestMaxAge.Value, requestMaxStale.Value);
                        return true;
                    }

                    context.Logger.LogExpirationMaxAgeExceeded(age, lowestMaxAge.Value);
                    return false;
                }
                else if (!cachedMaxAge.HasValue && !requestMaxAge.HasValue)
                {
                    // Validate expiration
                    var responseTime = context.ResponseTime.Value;
                    var expires = context.CachedResponseHeaders.Expires;
                    if (responseTime >= expires)
                    {
                        context.Logger.LogExpirationExpiresExceeded(responseTime, expires.Value);
                        return false;
                    }
                }
            }

            return true;
        }

        private bool TryParseValue(int startIndex, string header, out int value)
        {
            while (startIndex != header.Length)
            {
                if (header[startIndex] == '=')
                {
                    break;
                }
                ++startIndex;
            }
            if (startIndex != header.Length)
            {
                var endIndex = startIndex + 1;
                while (endIndex < header.Length)
                {
                    var cc = header[endIndex];
                    if ((cc >= '0') && (cc <= '9'))
                    {
                        endIndex++;
                    }
                    else
                    {
                        break;
                    }
                }
                value = int.Parse(header.Substring(startIndex + 1, endIndex - (startIndex + 1)), NumberStyles.None, NumberFormatInfo.InvariantInfo);
                return true;
            }
            value = 0;
            return false;
        }
    }
}

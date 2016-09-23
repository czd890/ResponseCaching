﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.AspNetCore.ResponseCaching.Internal;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.ResponseCaching
{
    public class ResponseCacheMiddleware
    {
        private static readonly TimeSpan DefaultExpirationTimeSpan = TimeSpan.FromSeconds(10);

        private readonly RequestDelegate _next;
        private readonly IResponseCacheStore _store;
        private readonly ResponseCacheOptions _options;
        private readonly IResponseCachePolicyProvider _policyProvider;
        private readonly IResponseCacheKeyProvider _keyProvider;
        private readonly Func<object, Task> _onStartingCallback;

        public ResponseCacheMiddleware(
            RequestDelegate next,
            IResponseCacheStore store,
            IOptions<ResponseCacheOptions> options,
            IResponseCachePolicyProvider policyProvider,
            IResponseCacheKeyProvider keyProvider)
        {
            if (next == null)
            {
                throw new ArgumentNullException(nameof(next));
            }
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            if (policyProvider == null)
            {
                throw new ArgumentNullException(nameof(policyProvider));
            }
            if (keyProvider == null)
            {
                throw new ArgumentNullException(nameof(keyProvider));
            }

            _next = next;
            _store = store;
            _options = options.Value;
            _policyProvider = policyProvider;
            _keyProvider = keyProvider;
            _onStartingCallback = state => OnResponseStartingAsync((ResponseCacheContext)state);
        }

        public async Task Invoke(HttpContext httpContext)
        {
            var context = new ResponseCacheContext(httpContext);

            // Should we attempt any caching logic?
            if (_policyProvider.IsRequestCacheable(context))
            {
                // Can this request be served from cache?
                if (await TryServeFromCacheAsync(context))
                {
                    return;
                }

                // Hook up to listen to the response stream
                ShimResponseStream(context);

                try
                {
                    // Subscribe to OnStarting event
                    httpContext.Response.OnStarting(_onStartingCallback, context);

                    await _next(httpContext);

                    // If there was no response body, check the response headers now. We can cache things like redirects.
                    await OnResponseStartingAsync(context);

                    // Finalize the cache entry
                    await FinalizeCacheBodyAsync(context);
                }
                finally
                {
                    UnshimResponseStream(context);
                }
            }
            else
            {
                await _next(httpContext);
            }
        }

        internal async Task<bool> TryServeCachedResponseAsync(ResponseCacheContext context, CachedResponse cachedResponse)
        {
            context.CachedResponse = cachedResponse;
            context.CachedResponseHeaders = cachedResponse.Headers;//new ResponseHeaders(cachedResponse.Headers);
            context.ResponseTime = _options.SystemClock.UtcNow;
            var cachedEntryAge = context.ResponseTime - context.CachedResponse.Created;
            context.CachedEntryAge = cachedEntryAge > TimeSpan.Zero ? cachedEntryAge : TimeSpan.Zero;

            if (_policyProvider.IsCachedEntryFresh(context))
            {
                // Check conditional request rules
                if (ConditionalRequestSatisfied(context))
                {
                    context.HttpContext.Response.StatusCode = StatusCodes.Status304NotModified;
                }
                else
                {
                    var response = context.HttpContext.Response;
                    // Copy the cached status code and response headers
                    response.StatusCode = context.CachedResponse.StatusCode;
                    foreach (var header in context.CachedResponse.Headers)
                    {
                        response.Headers.Add(header);
                    }

                    response.Headers[HeaderNames.Age] = context.CachedEntryAge.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture);

                    var body = context.CachedResponse.Body ??
                        ((CachedResponseBody) await _store.GetAsync(context.CachedResponse.BodyKeyPrefix))?.Body;

                    // If the body is not found, something went wrong.
                    if (body == null)
                    {
                        return false;
                    }

                    // Copy the cached response body
                    if (body.Length > 0)
                    {
                        // Add a content-length if required
                        if (!response.ContentLength.HasValue && StringValues.IsNullOrEmpty(response.Headers[HeaderNames.TransferEncoding]))
                        {
                            response.ContentLength = body.Length;
                        }
                        await response.Body.WriteAsync(body, 0, body.Length);
                    }
                }

                return true;
            }

            return false;
        }

        internal async Task<bool> TryServeFromCacheAsync(ResponseCacheContext context)
        {
            context.BaseKey = _keyProvider.CreateBaseKey(context);
            var cacheEntry = await _store.GetAsync(context.BaseKey);

            if (cacheEntry is CachedVaryByRules)
            {
                // Request contains vary rules, recompute key(s) and try again
                context.CachedVaryByRules = (CachedVaryByRules)cacheEntry;

                foreach (var varyKey in _keyProvider.CreateLookupVaryByKeys(context))
                {
                    cacheEntry = await _store.GetAsync(varyKey);

                    if (cacheEntry is CachedResponse && await TryServeCachedResponseAsync(context, (CachedResponse)cacheEntry))
                    {
                        return true;
                    }
                }
            }
            else if (cacheEntry is CachedResponse && await TryServeCachedResponseAsync(context, (CachedResponse)cacheEntry))
            {
                return true;
            }


            foreach (var c in context.HttpContext.Request.Headers[HeaderNames.CacheControl])
            {
                if (c.Equals("only-if-cached"))
                {
                    context.HttpContext.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                    return true;
                }
            }
            // if (context.RequestCacheControlHeaderValue.OnlyIfCached)
            // {
            //     context.HttpContext.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            //     return true;
            // }

            return false;
        }

        internal async Task FinalizeCacheHeadersAsync(ResponseCacheContext context)
        {
            if (_policyProvider.IsResponseCacheable(context))
            {
                context.ShouldCacheResponse = true;

                // Create the cache entry now
                var response = context.HttpContext.Response;
                var varyHeaderValue = new StringValues(response.Headers.GetCommaSeparatedValues(HeaderNames.Vary));
                var varyParamsValue = context.HttpContext.GetResponseCacheFeature()?.VaryByParams ?? StringValues.Empty;
                context.CachedResponseValidFor = context.ResponseCacheControlHeaderValue.SharedMaxAge ??
                    context.ResponseCacheControlHeaderValue.MaxAge ??
                    (context.ResponseExpires - context.ResponseTime) ??
                    DefaultExpirationTimeSpan;

                // Check if any vary rules exist
                if (!StringValues.IsNullOrEmpty(varyHeaderValue) || !StringValues.IsNullOrEmpty(varyParamsValue))
                {
                    // Normalize order and casing of vary by rules
                    var normalizedVaryHeaderValue = GetOrderCasingNormalizedStringValues(varyHeaderValue);
                    var normalizedVaryParamsValue = GetOrderCasingNormalizedStringValues(varyParamsValue);

                    // Update vary rules if they are different
                    if (context.CachedVaryByRules == null ||
                        !StringValues.Equals(context.CachedVaryByRules.Params, normalizedVaryParamsValue) ||
                        !StringValues.Equals(context.CachedVaryByRules.Headers, normalizedVaryHeaderValue))
                    {
                        context.CachedVaryByRules = new CachedVaryByRules
                        {
                            VaryByKeyPrefix = FastGuid.NewGuid().IdString,
                            Headers = normalizedVaryHeaderValue,
                            Params = normalizedVaryParamsValue
                        };
                    }

                    // Always overwrite the CachedVaryByRules to update the expiry information
                    await _store.SetAsync(context.BaseKey, context.CachedVaryByRules, context.CachedResponseValidFor);

                    context.StorageVaryKey = _keyProvider.CreateStorageVaryByKey(context);
                }

                // Ensure date header is set
                if (!context.ResponseDate.HasValue)
                {
                    context.ResponseDate = context.ResponseTime;
                    // Setting the date on the raw response headers.
                    context.TypedResponseHeaders.Date = context.ResponseDate;
                }

                // Store the response on the state
                context.CachedResponse = new CachedResponse
                {
                    BodyKeyPrefix = FastGuid.NewGuid().IdString,
                    Created = context.ResponseDate.Value,
                    StatusCode = context.HttpContext.Response.StatusCode
                };

                foreach (var header in context.TypedResponseHeaders.Headers)
                {
                    if (!string.Equals(header.Key, HeaderNames.Age, StringComparison.OrdinalIgnoreCase))
                    {
                        context.CachedResponse.Headers.Add(header);
                    }
                }
            }
            else
            {
                context.ResponseCacheStream.DisableBuffering();
            }
        }

        internal async Task FinalizeCacheBodyAsync(ResponseCacheContext context)
        {
            var contentLength = context.TypedResponseHeaders.ContentLength;
            if (context.ShouldCacheResponse &&
                context.ResponseCacheStream.BufferingEnabled &&
                (!contentLength.HasValue || contentLength == context.ResponseCacheStream.BufferedStream.Length))
            {
                if (context.ResponseCacheStream.BufferedStream.Length >= _options.MinimumSplitBodySize)
                {
                    // Store response and response body separately
                    await _store.SetAsync(context.StorageVaryKey ?? context.BaseKey, context.CachedResponse, context.CachedResponseValidFor);

                    var cachedResponseBody = new CachedResponseBody()
                    {
                        Body = context.ResponseCacheStream.BufferedStream.ToArray()
                    };

                    await _store.SetAsync(context.CachedResponse.BodyKeyPrefix, cachedResponseBody, context.CachedResponseValidFor);
                }
                else
                {
                    // Store response and response body together
                    context.CachedResponse.Body = context.ResponseCacheStream.BufferedStream.ToArray();
                    await _store.SetAsync(context.StorageVaryKey ?? context.BaseKey, context.CachedResponse, context.CachedResponseValidFor);
                }
            }
        }

        internal Task OnResponseStartingAsync(ResponseCacheContext context)
        {
            if (!context.ResponseStarted)
            {
                context.ResponseStarted = true;
                context.ResponseTime = _options.SystemClock.UtcNow;

                return FinalizeCacheHeadersAsync(context);
            }
            else
            {
                return TaskCache.CompletedTask;
            }
        }

        internal void ShimResponseStream(ResponseCacheContext context)
        {
            // Shim response stream
            context.OriginalResponseStream = context.HttpContext.Response.Body;
            context.ResponseCacheStream = new ResponseCacheStream(context.OriginalResponseStream, _options.MaximumCachedBodySize);
            context.HttpContext.Response.Body = context.ResponseCacheStream;

            // Shim IHttpSendFileFeature
            context.OriginalSendFileFeature = context.HttpContext.Features.Get<IHttpSendFileFeature>();
            if (context.OriginalSendFileFeature != null)
            {
                context.HttpContext.Features.Set<IHttpSendFileFeature>(new SendFileFeatureWrapper(context.OriginalSendFileFeature, context.ResponseCacheStream));
            }

            context.HttpContext.AddResponseCacheFeature();
        }

        internal static void UnshimResponseStream(ResponseCacheContext context)
        {
            // Unshim response stream
            context.HttpContext.Response.Body = context.OriginalResponseStream;

            // Unshim IHttpSendFileFeature
            context.HttpContext.Features.Set(context.OriginalSendFileFeature);

            context.HttpContext.RemoveResponseCacheFeature();
        }

        internal static bool ConditionalRequestSatisfied(ResponseCacheContext context)
        {
            var cachedResponseHeaders = context.CachedResponseHeaders;
            var ifNoneMatchHeader = context.HttpContext.Request.Headers[HeaderNames.IfNoneMatch];//context.TypedRequestHeaders.IfNoneMatch;

            if (!StringValues.IsNullOrEmpty(ifNoneMatchHeader))
            {
                if (ifNoneMatchHeader.Count == 1 && ifNoneMatchHeader[0].Equals(EntityTagHeaderValue.Any))
                {
                    return true;
                }

                if (!StringValues.IsNullOrEmpty(cachedResponseHeaders[HeaderNames.ETag]))
                {
                    EntityTagHeaderValue eTag;
                    if (EntityTagHeaderValue.TryParse(cachedResponseHeaders[HeaderNames.ETag], out eTag))
                    {
                        foreach (var tag in ifNoneMatchHeader)
                        {
                            EntityTagHeaderValue eetag;
                            if (EntityTagHeaderValue.TryParse(tag, out eetag))
                            {
                                if (eTag.Compare(eetag, useStrongComparison: true))
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                var ifUnmodifiedSince = context.HttpContext.Request.Headers[HeaderNames.IfUnmodifiedSince];//context.TypedRequestHeaders.IfUnmodifiedSince;
                if (!StringValues.IsNullOrEmpty(ifUnmodifiedSince))// && (cachedResponseHeaders.LastModified ?? cachedResponseHeaders.Date) <= ifUnmodifiedSince)
                {
                    DateTimeOffset modified;
                    if (!DateTimeOffset.TryParse(cachedResponseHeaders[HeaderNames.LastModified], out modified))
                    {
                        if (!DateTimeOffset.TryParse(cachedResponseHeaders[HeaderNames.Date], out modified))
                        {
                            return false;
                        }
                    }
                    DateTimeOffset unmodifiedSince;
                    if (DateTimeOffset.TryParse(ifUnmodifiedSince, out unmodifiedSince))
                    {
                        if (modified <= unmodifiedSince)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        // Normalize order and casing
        internal static StringValues GetOrderCasingNormalizedStringValues(StringValues stringValues)
        {
            if (stringValues.Count == 1)
            {
                return new StringValues(stringValues.ToString().ToUpperInvariant());
            }
            else
            {
                var originalArray = stringValues.ToArray();
                var newArray = new string[originalArray.Length];

                for (int i = 0; i < originalArray.Length; i++)
                {
                    newArray[i] = originalArray[i].ToUpperInvariant();
                }

                // Since the casing has already been normalized, use Ordinal comparison
                Array.Sort(newArray, StringComparer.Ordinal);

                return new StringValues(newArray);
            }
        }
    }
}

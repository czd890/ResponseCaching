// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.ResponseCaching.Internal
{
    public class ResponseCachingContext
    {
        private DateTimeOffset? _responseDate;
        private bool _parsedResponseDate;
        private DateTimeOffset? _responseExpires;
        private bool _parsedResponseExpires;
        private TimeSpan? _responseSharedMaxAge;
        private bool _parsedResponseSharedMaxAge;
        private TimeSpan? _responseMaxAge;
        private bool _parsedResponseMaxAge;

        internal ResponseCachingContext(HttpContext httpContext, ILogger logger)
        {
            HttpContext = httpContext;
            Logger = logger;
        }

        public HttpContext HttpContext { get; }

        public DateTimeOffset? ResponseTime { get; internal set; }

        public TimeSpan? CachedEntryAge { get; internal set; }

        public CachedVaryByRules CachedVaryByRules { get; internal set; }

        internal ILogger Logger { get; }

        internal bool ShouldCacheResponse { get;  set; }

        internal string BaseKey { get;  set; }

        internal string StorageVaryKey { get;  set; }

        internal TimeSpan CachedResponseValidFor { get;  set; }

        internal CachedResponse CachedResponse { get;  set; }

        internal bool ResponseStarted { get; set; }

        internal Stream OriginalResponseStream { get; set; }

        internal ResponseCachingStream ResponseCachingStream { get; set; }

        internal IHttpSendFileFeature OriginalSendFileFeature { get; set; }

        internal IHeaderDictionary CachedResponseHeaders { get; set; }

        internal DateTimeOffset? ResponseDate
        {
            get
            {
                if (!_parsedResponseDate)
                {
                    _parsedResponseDate = true;
                    DateTimeOffset date;
                    if (ParsingHelpers.TryStringToDate(HttpContext.Response.Headers[HeaderNames.Date], out date))
                    {
                        _responseDate = date;
                    }
                    else
                    {
                        _responseDate = null;
                    }
                }
                return _responseDate;
            }
            set
            {
                // Don't reparse the response date again if it's explicitly set
                _parsedResponseDate = true;
                _responseDate = value;
            }
        }

        internal DateTimeOffset? ResponseExpires
        {
            get
            {
                if (!_parsedResponseExpires)
                {
                    _parsedResponseExpires = true;
                    DateTimeOffset expires;
                    if (ParsingHelpers.TryStringToDate(HttpContext.Response.Headers[HeaderNames.Expires], out expires))
                    {
                        _responseExpires = expires;
                    }
                    else
                    {
                        _responseExpires = null;
                    }
                }
                return _responseExpires;
            }
        }

        internal TimeSpan? ResponseSharedMaxAge
        {
            get
            {
                if (!_parsedResponseSharedMaxAge)
                {
                    _parsedResponseSharedMaxAge = true;
                    _responseSharedMaxAge = null;
                    foreach (var header in HttpContext.Response.Headers[HeaderNames.CacheControl])
                    {
                        var index = header.IndexOf(CacheControlValues.SharedMaxAgeString, StringComparison.OrdinalIgnoreCase);
                        if (index != -1)
                        {
                            index += CacheControlValues.SharedMaxAgeString.Length;
                            int seconds;
                            if (ParsingHelpers.TryParseHeaderValue(index, header, out seconds))
                            {
                                _responseSharedMaxAge = TimeSpan.FromSeconds(seconds);
                            }
                            break;
                        }
                    }
                }
                return _responseSharedMaxAge;
            }
        }

        internal TimeSpan? ResponseMaxAge
        {
            get
            {
                if (!_parsedResponseMaxAge)
                {
                    _parsedResponseMaxAge = true;
                    _responseMaxAge = null;
                    foreach (var header in HttpContext.Response.Headers[HeaderNames.CacheControl])
                    {
                        var index = header.IndexOf(CacheControlValues.MaxAgeString, StringComparison.OrdinalIgnoreCase);
                        if (index != -1)
                        {
                            index += CacheControlValues.MaxAgeString.Length;
                            int seconds;
                            if (ParsingHelpers.TryParseHeaderValue(index, header, out seconds))
                            {
                                _responseMaxAge = TimeSpan.FromSeconds(seconds);
                            }
                            break;
                        }
                    }
                }
                return _responseMaxAge;
            }
        }
    }
}

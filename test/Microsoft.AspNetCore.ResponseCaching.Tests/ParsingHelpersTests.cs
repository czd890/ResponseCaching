// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.AspNetCore.ResponseCaching.Internal;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Microsoft.AspNetCore.ResponseCaching.Tests
{
    public class ParsingHelpersTests
    {
        [Theory]
        [InlineData("h=1", "h", 1)]
        [InlineData("header1=3, header2=10", "header1", 3)]
        [InlineData("header1   =45, header2=80", "header1", 45)]
        [InlineData("header1=   89   , header2=22", "header1", 89)]
        [InlineData("header1=   89   , header2= 42", "header2", 42)]
        void TryGetHeaderValue_Succeeds(string headerValue, string headerName, int expectedValue)
        {
            int value;
            Assert.True(HttpHeaderParsingHelpers.TryGetHeaderValue(new StringValues(headerValue), headerName, out value));
            Assert.Equal(expectedValue, value);
        }

        [Theory]
        [InlineData("h=", "h")]
        [InlineData("header1=, header2=10", "header1")]
        [InlineData("header1   , header2=80", "header1")]
        [InlineData("h=10", "header")]
        [InlineData("", "")]
        [InlineData(null, null)]
        void TryGetHeaderValue_Fails(string headerValue, string headerName)
        {
            int value;
            Assert.False(HttpHeaderParsingHelpers.TryGetHeaderValue(new StringValues(headerValue), headerName, out value));
        }
    }
}
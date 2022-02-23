﻿using System;
using Microsoft.Extensions.Primitives;
using Xunit;
using JobBank.Server;

namespace JobBank.Tests
{
    public class ContentNegotiationTests
    {
        [Fact]
        public void SelectBestFormat()
        {
            var available = new ContentFormatInfo[]
            {
                new("application/messagepack", ContentPreference.Best),
                new("application/json", ContentPreference.Good),
                new("application/xml", ContentPreference.Bad)
            };

            var requests = new StringValues(new string[]
            {
                // Deliberate use some strange syntax here to check that
                // parsing is RFC-correct
                @"text/plain, application/messagepack; dummy=""5\"","", application/json ,",
                "application/xml",
                ", multipart/mixed, application/*"
            });

            int best = ContentFormatInfo.Negotiate(available, requests);
            Assert.Equal(1, best);
        }

        [Fact]
        public void SelectBestFormat2()
        {
            var available = new ContentFormatInfo[]
            {
                new(mediaType: "application/jwt", ContentPreference.Best),
                new(mediaType: "application/json", ContentPreference.Good),
                new(mediaType: "text/plain", ContentPreference.Fair),
                new(mediaType: "application/xhtml+xml", ContentPreference.Fair)
            };

            // This Accept line comes from Microsoft Edge
            var requests = new StringValues(
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.9");

            int best = ContentFormatInfo.Negotiate(available, requests);
            Assert.Equal(3, best);
        }
    }
}

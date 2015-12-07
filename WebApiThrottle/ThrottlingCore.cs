﻿using Microsoft.Owin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.ServiceModel.Channels;
using System.Text;
using System.Threading.Tasks;
using System.Web;

namespace WebApiThrottle
{
    /// <summary>
    /// Common code shared between ThrottlingHandler and ThrottlingFilter
    /// </summary>
    internal static class ThrottlingCore
    {
        public class ThrottleDecision
        {
            public long RateLimit { get; set; }
            public RateLimitPeriod RateLimitPeriod { get; set; }
            public DateTime StartOfLimitPeriod { get; set; }
            public string RetryAfter { get; set; }
        }

        /// <summary>
        /// Common processing for throttling filter/handler/middleware.
        /// </summary>
        /// <param name="policyRepository">
        /// Policy store from which to read policy. Can be null if the <c>policy</c>
        /// argument is non-null.
        /// </param>
        /// <param name="policy">
        /// A policy to use if the <c>policyRepository</c> argument is null.
        /// </param>
        /// <param name="request">
        /// The request, or null if an <c>HttpRequestMessage</c> is unavailable (e.g., because
        /// we're using OWIN). This is only used for logging, so passing null is not a big problem.
        /// </param>
        /// <param name="identity">
        /// Information used to distinguish which groups of users have their usage grouped under
        /// the same counter.
        /// </param>
        /// <param name="getAdjustedLimitForPeriod">
        /// Enables the limit for a particular period to be overridden. (Used by the <see cref="ThrottlingFilter"/>
        /// because each application of that attribute can specify its own limits that may differ from configured
        /// policy.)
        /// </param>
        /// <param name="logger">
        /// Used to log cases where we block users.
        /// </param>
        /// <returns></returns>
        internal static ThrottleDecision ProcessRequest(
            IPolicyRepository policyRepository,
            ThrottlePolicy policy,
            IThrottleRepository throttleRepository,
            HttpRequestMessage request,
            RequestIdentity identity,
            Func<RateLimitPeriod, long> getAdjustedLimitForPeriod,
            IThrottleLogger logger)
        {
            // get policy from repo
            if (policyRepository != null)
            {
                policy = policyRepository.FirstOrDefault(ThrottleManager.GetPolicyKey());
            }

            bool applyThrottling = policy.IpThrottling || policy.ClientThrottling || policy.EndpointThrottling;
            if (applyThrottling && !IsWhitelisted(policy, identity))
            {

                // get default rates
                var defRates = RatesWithDefaults(policy.Rates.ToList());
                if (policy.StackBlockedRequests)
                {
                    // all requests including the rejected ones will stack in this order: week, day, hour, min, sec
                    // if a client hits the hour limit then the minutes and seconds counters will expire and will eventually get erased from cache
                    defRates.Reverse();
                }

                // apply policy
                foreach (var rate in defRates)
                {
                    RateLimitPeriod rateLimitPeriod = rate.Key;
                    long rateLimit = rate.Value;

                    TimeSpan timeSpan = GetTimeSpanFromPeriod(rateLimitPeriod);

                    long adjustedLimit = getAdjustedLimitForPeriod(rateLimitPeriod);
                    if (adjustedLimit > 0)
                    {
                        rateLimit = adjustedLimit;
                    }

                    // apply global rules
                    ApplyRules(policy, identity, timeSpan, rateLimitPeriod, ref rateLimit);

                    if (rateLimit > 0)
                    {
                        // increment counter
                        string requestId;
                        ThrottleCounter throttleCounter = GetThrottleCounter(throttleRepository, policy, identity, timeSpan, rateLimitPeriod, out requestId);

                        // check if limit is reached
                        if (throttleCounter.TotalRequests > rateLimit)
                        {
                            // log blocked request
                            if (logger != null)
                            {
                                logger.Log(ComputeLogEntry(requestId, identity, throttleCounter, rateLimitPeriod.ToString(), rateLimit, request));
                            }

                            return new ThrottleDecision
                            {
                                RateLimit = rateLimit,
                                RateLimitPeriod = rateLimitPeriod,
                                StartOfLimitPeriod = throttleCounter.Timestamp,
                                RetryAfter = RetryAfterFrom(throttleCounter.Timestamp, rateLimitPeriod)
                            };
                        }
                    }
                }
            }

            return null;
        }

        internal static bool IncludeDefaultClientKeyHeaders(string headerName)
        {
            // Note: Stefan Prodan's original used "Authorization-Token" as the default header
            // from which the ClientKey was calculated. But that's not a standard HTTP header.
            // So I've changed this to Authorization, which is used for all standard auth,
            // including OAuth 2.
            return headerName == "Authorization";
        }

        internal static RequestIdentity GetIdentity(HttpRequestMessage request, Func<string, bool> includeHeaderInClientKey)
        {
            var entry = new RequestIdentity();
            entry.ClientIp = GetClientIp(request).ToString();
            entry.Endpoint = request.RequestUri.AbsolutePath.ToLowerInvariant();
            entry.ClientKey = request.Headers
                .Where(h => includeHeaderInClientKey(h.Key))
                .Aggregate(default(string), (k, h) => k + h.Value.First())
                ?? "anon";

            return entry;
        }

        internal static RequestIdentity GetIdentity(IOwinRequest request, Func<string, bool> includeHeaderInClientKey)
        {
            var entry = new RequestIdentity();
            entry.ClientIp = request.RemoteIpAddress;
            entry.Endpoint = request.Uri.AbsolutePath.ToLowerInvariant();
            entry.ClientKey = request.Headers
                .Where(h => includeHeaderInClientKey(h.Key))
                .Aggregate(default(string), (k, h) => k + h.Value.First())
                ?? "anon";

            return entry;
        }

        private static bool ContainsIp(List<string> ipRules, string clientIp)
        {
            var ip = IPAddress.Parse(clientIp);
            if (ipRules != null && ipRules.Any())
            {
                foreach (var rule in ipRules)
                {
                    var range = new IPAddressRange(rule);
                    if (range.Contains(ip))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool ContainsIp(List<string> ipRules, string clientIp, out string rule)
        {
            rule = null;
            var ip = IPAddress.Parse(clientIp);
            if (ipRules != null && ipRules.Any())
            {
                foreach (var r in ipRules)
                {
                    var range = new IPAddressRange(r);
                    if (range.Contains(ip))
                    {
                        rule = r;
                        return true;
                    }
                }
            }

            return false;
        }

        private static IPAddress GetClientIp(HttpRequestMessage request)
        {
            IPAddress ipAddress;

            if (request.Properties.ContainsKey("MS_HttpContext"))
            {
                var ok = IPAddress.TryParse(((HttpContextBase)request.Properties["MS_HttpContext"]).Request.UserHostAddress, out ipAddress);

                if (ok)
                {
                    return ipAddress;
                }
            }

            if (request.Properties.ContainsKey(RemoteEndpointMessageProperty.Name))
            {
                var ok = IPAddress.TryParse(((RemoteEndpointMessageProperty)request.Properties[RemoteEndpointMessageProperty.Name]).Address, out ipAddress);

                if (ok)
                {
                    return ipAddress;
                }
            }

            if (request.Properties.ContainsKey("MS_OwinContext"))
            {
                var ok = IPAddress.TryParse(((Microsoft.Owin.OwinContext)request.Properties["MS_OwinContext"]).Request.RemoteIpAddress, out ipAddress);

                if (ok)
                {
                    return ipAddress;
                }
            }


            return null;
        }

        private static ThrottleLogEntry ComputeLogEntry(string requestId, RequestIdentity identity, ThrottleCounter throttleCounter, string rateLimitPeriod, long rateLimit, HttpRequestMessage request)
        {
            return new ThrottleLogEntry
            {
                ClientIp = identity.ClientIp,
                ClientKey = identity.ClientKey,
                Endpoint = identity.Endpoint,
                LogDate = DateTime.UtcNow,
                RateLimit = rateLimit,
                RateLimitPeriod = rateLimitPeriod,
                RequestId = requestId,
                StartPeriod = throttleCounter.Timestamp,
                TotalRequests = throttleCounter.TotalRequests,
                Request = request
            };
        }

        private static string RetryAfterFrom(DateTime timestamp, RateLimitPeriod period)
        {
            var secondsPast = Convert.ToInt32((DateTime.UtcNow - timestamp).TotalSeconds);
            var retryAfter = 1;
            switch (period)
            {
                case RateLimitPeriod.Minute:
                    retryAfter = 60;
                    break;
                case RateLimitPeriod.Hour:
                    retryAfter = 60 * 60;
                    break;
                case RateLimitPeriod.Day:
                    retryAfter = 60 * 60 * 24;
                    break;
                case RateLimitPeriod.Week:
                    retryAfter = 60 * 60 * 24 * 7;
                    break;
            }
            retryAfter = retryAfter > 1 ? retryAfter - secondsPast : 1;
            return retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool IsWhitelisted(ThrottlePolicy policy, RequestIdentity requestIdentity)
        {
            if (policy.IpThrottling)
            {
                if (policy.IpWhitelist != null && ContainsIp(policy.IpWhitelist, requestIdentity.ClientIp))
                {
                    return true;
                }
            }

            if (policy.ClientThrottling)
            {
                if (policy.ClientWhitelist != null && policy.ClientWhitelist.Contains(requestIdentity.ClientKey))
                {
                    return true;
                }
            }

            if (policy.EndpointThrottling)
            {
                if (policy.EndpointWhitelist != null
                    && policy.EndpointWhitelist.Any(x => requestIdentity.Endpoint.Contains(x.ToLowerInvariant())))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ComputeThrottleKey(ThrottlePolicy policy, RequestIdentity requestIdentity, RateLimitPeriod period)
        {
            var keyValues = new List<string>()
                {
                    ThrottleManager.GetThrottleKey()
                };

            if (policy.IpThrottling)
            {
                keyValues.Add(requestIdentity.ClientIp);
            }

            if (policy.ClientThrottling)
            {
                keyValues.Add(requestIdentity.ClientKey);
            }

            if (policy.EndpointThrottling)
            {
                keyValues.Add(requestIdentity.Endpoint);
            }

            keyValues.Add(period.ToString());

            var id = string.Join("_", keyValues);
            var idBytes = Encoding.UTF8.GetBytes(id);

            byte[] hashBytes;

            using (var algorithm = System.Security.Cryptography.HashAlgorithm.Create("SHA1"))
            {
                hashBytes = algorithm.ComputeHash(idBytes);
            }
            
            var hex = BitConverter.ToString(hashBytes).Replace("-", string.Empty);
            return hex;
        }

        private static List<KeyValuePair<RateLimitPeriod, long>> RatesWithDefaults(List<KeyValuePair<RateLimitPeriod, long>> defRates)
        {
            if (!defRates.Any(x => x.Key == RateLimitPeriod.Second))
            {
                defRates.Insert(0, new KeyValuePair<RateLimitPeriod, long>(RateLimitPeriod.Second, 0));
            }

            if (!defRates.Any(x => x.Key == RateLimitPeriod.Minute))
            {
                defRates.Insert(1, new KeyValuePair<RateLimitPeriod, long>(RateLimitPeriod.Minute, 0));
            }

            if (!defRates.Any(x => x.Key == RateLimitPeriod.Hour))
            {
                defRates.Insert(2, new KeyValuePair<RateLimitPeriod, long>(RateLimitPeriod.Hour, 0));
            }

            if (!defRates.Any(x => x.Key == RateLimitPeriod.Day))
            {
                defRates.Insert(3, new KeyValuePair<RateLimitPeriod, long>(RateLimitPeriod.Day, 0));
            }

            if (!defRates.Any(x => x.Key == RateLimitPeriod.Week))
            {
                defRates.Insert(4, new KeyValuePair<RateLimitPeriod, long>(RateLimitPeriod.Week, 0));
            }

            return defRates;
        }

        private static ThrottleCounter GetThrottleCounter(
            IThrottleRepository repository,
            ThrottlePolicy policy,
            RequestIdentity requestIdentity,
            TimeSpan timeSpan,
            RateLimitPeriod period,
            out string id)
        {
            id = ComputeThrottleKey(policy, requestIdentity, period);
            return repository.IncrementAndGet(id, timeSpan);
        }

        private static TimeSpan GetTimeSpanFromPeriod(RateLimitPeriod rateLimitPeriod)
        {
            var timeSpan = TimeSpan.FromSeconds(1);

            switch (rateLimitPeriod)
            {
                case RateLimitPeriod.Second:
                    timeSpan = TimeSpan.FromSeconds(1);
                    break;
                case RateLimitPeriod.Minute:
                    timeSpan = TimeSpan.FromMinutes(1);
                    break;
                case RateLimitPeriod.Hour:
                    timeSpan = TimeSpan.FromHours(1);
                    break;
                case RateLimitPeriod.Day:
                    timeSpan = TimeSpan.FromDays(1);
                    break;
                case RateLimitPeriod.Week:
                    timeSpan = TimeSpan.FromDays(7);
                    break;
            }

            return timeSpan;
        }

        private static void ApplyRules(ThrottlePolicy policy, RequestIdentity identity, TimeSpan timeSpan, RateLimitPeriod rateLimitPeriod, ref long rateLimit)
        {
            // apply endpoint rate limits
            if (policy.EndpointRules != null)
            {
                var rules = policy.EndpointRules.Where(x => identity.Endpoint.Contains(x.Key.ToLowerInvariant())).ToList();
                if (rules.Any())
                {
                    // get the lower limit from all applying rules
                    var customRate = (from r in rules let rateValue = r.Value.GetLimit(rateLimitPeriod) select rateValue).Min();

                    if (customRate > 0)
                    {
                        rateLimit = customRate;
                    }
                }
            }

            // apply custom rate limit for clients that will override endpoint limits
            if (policy.ClientRules != null && policy.ClientRules.Keys.Contains(identity.ClientKey))
            {
                var limit = policy.ClientRules[identity.ClientKey].GetLimit(rateLimitPeriod);
                if (limit > 0)
                {
                    rateLimit = limit;
                }
            }

            // enforce ip rate limit as is most specific 
            string ipRule = null;
            if (policy.IpRules != null && ContainsIp(policy.IpRules.Keys.ToList(), identity.ClientIp, out ipRule))
            {
                var limit = policy.IpRules[ipRule].GetLimit(rateLimitPeriod);
                if (limit > 0)
                {
                    rateLimit = limit;
                }
            }
        }
    }
}

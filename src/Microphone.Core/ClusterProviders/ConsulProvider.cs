﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microphone.Core.ClusterProviders
{
    public class ConsulProvider : IClusterProvider
    {
        private readonly string _consulHost;
        private readonly int _consulPort;
        private readonly bool _useEbayFabio;
        private string _serviceId;
        private string _serviceName;
        private Uri _uri;
        private string _version;

        public ConsulProvider(string consulHost = "localhost", int consulPort = 8500, bool useEbayFabio = false)
        {
            _consulHost = consulHost;
            _consulPort = consulPort;
            _useEbayFabio = useEbayFabio;
        }

        private string RootUrl => $"http://{_consulHost}:{_consulPort}";
        private string CriticalServicesUrl => RootUrl + "/v1/health/state/critical";
        private string RegisterServiceUrl => RootUrl + "/v1/agent/service/register";

        public async Task<ServiceInformation[]> FindServiceInstancesAsync(string name)
        {
            if (_useEbayFabio)
            {
                return new[] {new ServiceInformation($"http://{_consulHost}", 9999)};
            }


            using (var client = new HttpClient())
            {
                var response = await client.GetAsync(ServiceHealthUrl(name));
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new Exception("Could not find services");
                }

                var body = await response.Content.ReadAsStringAsync();
                var res1 = JArray.Parse(body);
                var res =
                    res1.Select(
                        entry =>
                            new ServiceInformation(
                                entry["Service"]["Address"].Value<string>(),
                                entry["Service"]["Port"].Value<int>())
                        ).ToArray();

                return res;
            }
        }

        public async Task RegisterServiceAsync(string serviceName, string serviceId, string version, Uri uri)
        {
            _serviceName = serviceName;
            _serviceId = serviceId;
            _version = version;
            _uri = uri;

            var localIp = DnsUtils.GetLocalIPAddress();
            var check = "http://" + localIp + ":" + uri.Port + "/status";

            Logger.Information(
                $"Registering service on {localIp} on Consul {_consulHost}:{_consulPort} with status check {check}");
            var payload = new
            {
                ID = serviceId,
                Name = serviceName,
                Tags = new[] {$"urlprefix-/{serviceName}"},
                Address = localIp,
                // ReSharper disable once RedundantAnonymousTypePropertyName
                Port = uri.Port,
                Check = new
                {
                    HTTP = check,
                    Interval = "1s"
                }
            };

            using (var client = new HttpClient())
            {
                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json);

                var res = await client.PostAsync(RegisterServiceUrl, content);
                if (res.StatusCode != HttpStatusCode.OK)
                {
                    throw new Exception("Could not register service");
                }
                Logger.Information($"Registration successful");
            }
        }

        public Task BootstrapClientAsync()
        {
            return Task.FromResult(0);
        }

        public async Task KeyValuePutAsync(string key, object value)
        {
            using (var client = new HttpClient())
            {
                var json = JsonConvert.SerializeObject(value);
                var content = new StringContent(json);

                var response = await client.PutAsync(KeyValueUrl(key), content);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new Exception("Could not put value");
                }
            }
        }

        public async Task<T> KeyValueGetAsync<T>(string key)
        {
            using (var client = new HttpClient())
            {
                var response = await client.GetAsync(KeyValueUrl(key));

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new Exception($"There is no value for key \"{key}\"");
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new Exception("Could not get value");
                }

                var body = await response.Content.ReadAsStringAsync();
                var deserializedBody = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(body);
                var bytes = Convert.FromBase64String((string) deserializedBody[0]["Value"]);
                var strValue = Encoding.UTF8.GetString(bytes, 0, bytes.Length);

                return JsonConvert.DeserializeObject<T>(strValue);
            }
        }

        private string KeyValueUrl(string key) => RootUrl + "/v1/kv/" + key;
        private string ServiceHealthUrl(string service) => RootUrl + "/v1/health/service/" + service;
        private string DeregisterServiceUrl(string service) => RootUrl + "/v1/agent/service/deregister/" + service;
    }
}
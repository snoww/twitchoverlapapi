﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ChannelIntersection.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Z.EntityFramework.Plus;

namespace ChannelIntersection
{
    public static class Program
    {
        private static readonly HttpClient Http = new();
        private static string _twitchToken;
        private static string _twitchClient;
        private static string _psqlConnection;
        private static TwitchContext _context;
        private static readonly DateTime Timestamp = DateTime.UtcNow;

        public static async Task Main()
        {
            using (JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync("config.json")))
            {
                _twitchToken = json.RootElement.GetProperty("TWITCH_TOKEN").GetString();
                _twitchClient = json.RootElement.GetProperty("TWITCH_CLIENT").GetString();
                _psqlConnection = json.RootElement.GetProperty("POSTGRES").GetString();
            }
            
            await using var dbContext = new TwitchContext(_psqlConnection);
            _context = dbContext;
            await using IDbContextTransaction trans = await dbContext.Database.BeginTransactionAsync();
            Console.WriteLine($"connected to database at {Timestamp:u}");
            
            var sw = new Stopwatch();
            sw.Start();
            var timer = new Stopwatch();
            timer.Start();

            Dictionary<string, Channel> channels = await GetTopChannels();
            await GetChannelAvatar(channels);

            Console.WriteLine($"retrieved {channels.Count} channels in {sw.Elapsed.TotalSeconds}s");
            sw.Restart();

            var channelChatters = new ConcurrentDictionary<Channel, HashSet<string>>();
            var totalIntersectionCount = new ConcurrentDictionary<Channel, ConcurrentDictionary<string, byte>>();

            IEnumerable<Task> processTasks = channels.Select(async channel =>
            {
                (_, Channel ch) = channel;
                HashSet<string> chatters = await GetChatters(ch);
                if (chatters == null)
                {
                    return;
                }

                channelChatters.TryAdd(ch, chatters);
                totalIntersectionCount.TryAdd(ch, new ConcurrentDictionary<string, byte>());
            });

            await Task.WhenAll(processTasks);

            Console.WriteLine($"retrieved {channelChatters.Count} chatters in {sw.Elapsed.TotalSeconds}s");
            sw.Restart();

            var data = new ConcurrentBag<Overlap>();

            Parallel.ForEach(GetKCombs(new List<Channel>(channelChatters.Keys), 2), x =>
            {
                Channel[] pair = x.ToArray();
                int count = channelChatters[pair[0]].Count(y =>
                {
                    if (!channelChatters[pair[1]].Contains(y)) return false;
                    totalIntersectionCount[pair[0]].TryAdd(y, byte.MaxValue);
                    totalIntersectionCount[pair[1]].TryAdd(y, byte.MaxValue);
                    return true;
                });

                data.Add(new Overlap(Timestamp, pair[0].Id, pair[1].Id, count));
            });

            Console.WriteLine($"calculated intersection in {sw.Elapsed.TotalSeconds}s");
            sw.Restart();

            foreach ((Channel ch, _) in channelChatters)
            {
                ch.Shared = totalIntersectionCount[ch].Count;
            }

            dbContext.Channels.UpdateRange(channels.Values.ToList());
            await dbContext.Overlaps.AddRangeAsync(data);
            
            DateTime twoWeeks = Timestamp.AddDays(-14);
            await dbContext.Overlaps.Where(x => x.Timestamp <= twoWeeks).DeleteAsync();

            await dbContext.SaveChangesAsync();
            await trans.CommitAsync();

            Console.WriteLine($"inserted into database in {sw.Elapsed.TotalSeconds}s");
            sw.Restart();

            if (Timestamp.Minute >= 30) // only calculate union every hour
            {
                var rootPath = $"./channel-chatters/{Timestamp.Month}-{Timestamp.Year}";
                Directory.CreateDirectory(rootPath);
                
                Console.WriteLine("beginning unique chatter merge");
            
                foreach ((Channel channel, HashSet<string> chatters) in channelChatters)
                {
                    var path = $"{rootPath}/{channel.Id}.txt";
                    if (!File.Exists(path))
                    {
                        await File.WriteAllLinesAsync(path, chatters);
                        continue;
                    }
                    
                    var existingChatters = new HashSet<string>(File.ReadLines(path));
                    existingChatters.EnsureCapacity(existingChatters.Count + chatters.Count);
                    existingChatters.UnionWith(chatters);
                    await File.WriteAllLinesAsync(path, existingChatters);
                }
            
                Console.WriteLine($"union completed in {sw.Elapsed.TotalSeconds}s");
            }

            Console.WriteLine($"total time taken: {timer.Elapsed.TotalSeconds}s");
        }

        private static async Task<HashSet<string>> GetChatters(Channel channel)
        {
            Stream stream;
            try
            {
                stream = await Http.GetStreamAsync($"https://tmi.twitch.tv/group/user/{channel.LoginName}/chatters");
            }
            catch
            {
                Console.WriteLine($"Could not retrieve chatters for {channel.LoginName}");
                return null;
            }

            using JsonDocument response = await JsonDocument.ParseAsync(stream);
            await stream.DisposeAsync();

            int chatters = response.RootElement.GetProperty("chatter_count").GetInt32();
            if (chatters < 500)
            {
                return null;
            }

            channel.Chatters = chatters;
            var chatterList = new HashSet<string>(chatters);
            JsonElement.ObjectEnumerator viewerTypes = response.RootElement.GetProperty("chatters").EnumerateObject();
            foreach (JsonProperty viewerType in viewerTypes)
            {
                foreach (JsonElement viewer in viewerType.Value.EnumerateArray())
                {
                    string username = viewer.GetString()?.ToLower();
                    if (username == null || username.EndsWith("bot", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    chatterList.Add(username);
                }
            }

            return chatterList;
        }

        private static async Task<Dictionary<string, Channel>> GetTopChannels()
        {
            var channels = new Dictionary<string, Channel>();
            var newChannels = new List<Channel>();
            var pageToken = string.Empty;
            do
            {
                using var request = new HttpRequestMessage();
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _twitchToken);
                request.Headers.Add("Client-Id", _twitchClient);
                request.RequestUri = string.IsNullOrWhiteSpace(pageToken)
                    ? new Uri("https://api.twitch.tv/helix/streams?first=100")
                    : new Uri($"https://api.twitch.tv/helix/streams?first=100&after={pageToken}");

                using HttpResponseMessage response = await Http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                    pageToken = json.RootElement.GetProperty("pagination").GetProperty("cursor").GetString();
                    JsonElement.ArrayEnumerator channelEnumerator = json.RootElement.GetProperty("data").EnumerateArray();
                    foreach (JsonElement channel in channelEnumerator)
                    {
                        int viewerCount = channel.GetProperty("viewer_count").GetInt32();
                        if (viewerCount < 1500)
                        {
                            pageToken = null;
                            break;
                        }
                        string login = channel.GetProperty("user_login").GetString()?.ToLowerInvariant();
                        Channel dbChannel = await _context.Channels.SingleOrDefaultAsync(x => x.LoginName == login);
                        if (dbChannel == null)
                        {
                            dbChannel = new Channel(login, channel.GetProperty("user_name").GetString(), channel.GetProperty("game_name").GetString(), viewerCount, Timestamp);
                            newChannels.Add(dbChannel);
                        }
                        else
                        {
                            dbChannel.DisplayName = channel.GetProperty("user_name").GetString();
                            dbChannel.Game = channel.GetProperty("game_name").GetString();
                            dbChannel.Viewers = viewerCount;
                            dbChannel.LastUpdate = Timestamp;
                        }

                        channels.TryAdd(login, dbChannel);
                    }
                }
            } while (pageToken != null);

            await _context.AddRangeAsync(newChannels);
            await _context.SaveChangesAsync();
            return channels;
        }

        private static async Task GetChannelAvatar(Dictionary<string, Channel> channels)
        {
            using var http = new HttpClient();
            foreach (string reqString in RequestBuilder(channels.Keys.ToList()))
            {
                using var request = new HttpRequestMessage();
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _twitchToken);
                request.Headers.Add("Client-Id", _twitchClient);
                request.RequestUri = new Uri($"https://api.twitch.tv/helix/users?{reqString}");
                using HttpResponseMessage response = await http.SendAsync(request);
                using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                JsonElement.ArrayEnumerator data = json.RootElement.GetProperty("data").EnumerateArray();
                foreach (JsonElement channel in data)
                {
                    Channel model = channels[channel.GetProperty("login").GetString()!.ToLowerInvariant()];
                    if (model != null) model.Avatar = channel.GetProperty("profile_image_url").GetString()?.Replace("-300x300", "-70x70").Split('/')[4];
                }
            }
        }

        private static IEnumerable<string> RequestBuilder(IReadOnlyCollection<string> channels)
        {
            var shards = (int) Math.Ceiling(channels.Count / 100.0);
            var list = new List<string>(shards);
            for (int i = 0; i < shards; i++)
            {
                var request = new StringBuilder();
                foreach (string channel in channels.Skip(i * 100).Take(100))
                {
                    request.Append("&login=").Append(channel);
                }

                list.Add(request.ToString()[1..]);
            }

            return list;
        }

        private static IEnumerable<IEnumerable<T>> GetKCombs<T>(IEnumerable<T> list, int length) where T : IComparable
        {
            if (length == 1) return list.Select(t => new[] {t});
            return GetKCombs(list, length - 1)
                .SelectMany(t => list.Where(o => o.CompareTo(t.Last()) > 0),
                    (t1, t2) => t1.Concat(new[] {t2}));
        }
    }
}
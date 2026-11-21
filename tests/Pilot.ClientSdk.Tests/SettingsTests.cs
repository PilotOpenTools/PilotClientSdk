using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Pilot.ClientSdk.Infrastructure;
using Pilot.ClientSdk.Pilot;
using Pilot.ClientSdk.Telemetry;
using Xunit;

namespace Pilot.ClientSdk.Tests
{
    /// <summary>Общие настройки Pilot «Модули»: JSON, приоритет над файлом, проверка значения, смена адреса на лету.</summary>
    public class SettingsTests
    {
        [Fact]
        public void Json_round_trip_keeps_all_fields()
        {
            var config = new ModuleConfig
            {
                StatsEnabled = false,
                StatsUrl = "http://stats:5310/api/module-events",
                ApiKey = "k",
                FlushSeconds = 42,
                ReportLoadedModules = false,
                StartupWindowMode = ModuleConfig.WindowOnProblems,
                StartupLockSeconds = 7,
                StartupAutoClose = false,
                IgnoredModules = new[] { "OpenStoradge", "Старый модуль" },
                TrayMenu = false,
            };

            var back = ModuleConfig.Parse(config.ToJson());

            Assert.False(back.StatsEnabled);
            Assert.Equal("http://stats:5310/api/module-events", back.StatsUrl);
            Assert.Equal("k", back.ApiKey);
            Assert.Equal(42, back.FlushSeconds);
            Assert.False(back.ReportLoadedModules);
            Assert.Equal(ModuleConfig.WindowOnProblems, back.WindowMode);
            Assert.True(back.StartupWindow);
            Assert.Equal(7, back.StartupLockSeconds);
            Assert.False(back.StartupAutoClose);
            Assert.Equal(new[] { "OpenStoradge", "Старый модуль" }, back.IgnoredModules);
            Assert.False(back.TrayMenu);
        }

        [Fact]
        public async Task Settings_notifications_publish_current_before_running_handlers_outside_lock()
        {
            ModuleConfig.Reset(new ModuleConfig { StatsUrl = "http://previous/" });
            using var release = new ManualResetEventSlim();
            var notified = new TaskCompletionSource<ModuleConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ct = TestContext.Current.CancellationToken;
            ModuleConfig later = null;
            ModuleConfig.Changed += config =>
            {
                notified.TrySetResult(config);
                release.Wait(ct);
                throw new InvalidOperationException("handler failed");
            };
            ModuleConfig.Changed += config => later = config;
            var update = Task.Run(() => ModuleConfig.ApplyCommon("{\"statsUrl\":\"http://current/\"}"), ct);

            try
            {
                Assert.Same(notified.Task, await Task.WhenAny(notified.Task, Task.Delay(5000, ct)));
                var read = Task.Run(() => ModuleConfig.Current, ct);
                Assert.Same(read, await Task.WhenAny(read, Task.Delay(5000, ct)));
                Assert.Same(await notified.Task, await read);
            }
            finally
            {
                release.Set();
                await update;
                ModuleConfig.Reset();
            }

            Assert.Same(await notified.Task, later);
        }

        [Theory]
        [InlineData(0, -1)]
        [InlineData(int.MaxValue, 601)]
        public void Out_of_range_file_intervals_fall_back_without_discarding_other_settings(int flushSeconds, int startupLockSeconds)
        {
            var directory = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString())).FullName;
            var path = System.IO.Path.Combine(directory, ModuleConfig.FileName);
            try
            {
                var file = new ModuleConfig
                {
                    StatsUrl = "http://file-settings/",
                    ApiKey = "test-key",
                    FlushSeconds = flushSeconds,
                    StartupLockSeconds = startupLockSeconds,
                    TrayMenu = false,
                };
                System.IO.File.WriteAllText(path, file.ToJson());

                var loaded = ModuleConfig.Load(directory);
                Assert.Equal(file.StatsUrl, loaded.StatsUrl);
                Assert.Equal(file.ApiKey, loaded.ApiKey);
                Assert.Equal(ModuleConfig.DefaultFlushSeconds, loaded.FlushSeconds);
                Assert.Equal(ModuleConfig.DefaultStartupLockSeconds, loaded.StartupLockSeconds);
                Assert.False(loaded.TrayMenu);
            }
            finally
            {
                System.IO.File.Delete(path);
                System.IO.Directory.Delete(directory);
            }
        }

        [Fact]
        public void Window_mode_falls_back_to_old_flag()
        {
            Assert.Equal(ModuleConfig.WindowAlways, ModuleConfig.Parse("{}").WindowMode);
            Assert.Equal(ModuleConfig.WindowNever, ModuleConfig.Parse("{\"startupWindow\":false}").WindowMode);
            Assert.Equal(ModuleConfig.WindowOnProblems, ModuleConfig.Parse("{\"startupWindow\":false,\"startupWindowMode\":\"problems\"}").WindowMode);
            Assert.Equal(ModuleConfig.WindowAlways, ModuleConfig.Parse("{\"startupWindowMode\":\"нечто\"}").WindowMode);
            Assert.True(ModuleConfig.Parse("{}").TrayMenu);
            Assert.Empty(ModuleConfig.Parse("{}").IgnoredModules);
        }

        [Fact]
        public void Ignored_modules_match_part_of_name()
        {
            var config = new ModuleConfig { IgnoredModules = new[] { "storadge" } };
            Assert.True(config.IsIgnored("0 - Хранилище - (OpenStoradge) - (CICD)", null));
            Assert.True(config.IsIgnored(null, "087OpenStoradge.ext2"));
            Assert.False(config.IsIgnored("Reports", "112Reports.ext2"));
        }

        [Fact]
        public void Common_settings_override_file_and_empty_value_restores_it()
        {
            ModuleConfig.Reset(new ModuleConfig { StatsUrl = "http://file/api/module-events" });
            try
            {
                var changes = new List<ModuleConfig>();
                ModuleConfig.Changed += changes.Add;
                Assert.Equal("http://file/api/module-events", ModuleConfig.Current.StatsUrl);

                ModuleConfig.ApplyCommon("{\"statsUrl\":\"http://pilot-settings/api/module-events\",\"startupWindowMode\":\"never\"}");
                Assert.Equal("http://pilot-settings/api/module-events", ModuleConfig.Current.StatsUrl);
                Assert.Equal(ModuleConfig.WindowNever, ModuleConfig.Current.WindowMode);
                Assert.Equal("общие настройки Pilot", ModuleConfig.Current.Source);

                ModuleConfig.ApplyCommon("{ не json");                     // битое значение не сбрасывает действующее
                Assert.Equal("http://pilot-settings/api/module-events", ModuleConfig.Current.StatsUrl);

                ModuleConfig.ApplyCommon("{\"statsUrl\":\"http://invalid\",\"flushSeconds\":2147483647}");
                Assert.Equal("http://pilot-settings/api/module-events", ModuleConfig.Current.StatsUrl);

                ModuleConfig.ApplyCommon("");
                Assert.Equal("http://file/api/module-events", ModuleConfig.Current.StatsUrl);
                Assert.Equal(2, changes.Count);
            }
            finally
            {
                ModuleConfig.Reset();
            }
        }

        [Theory]
        [InlineData("", true)]
        [InlineData("null", false)]
        [InlineData("{\"statsUrl\":\"http://h:5310/api/module-events\"}", true)]
        [InlineData("{\"statsEnabled\":false}", true)]
        [InlineData("{\"statsUrl\":\"h:5310\"}", false)]
        [InlineData("{\"statsEnabled\":true}", false)]
        [InlineData("{\"statsUrl\":\"http://h\",\"flushSeconds\":0}", false)]
        [InlineData("{\"statsUrl\":\"http://h\",\"flushSeconds\":2147483647}", false)]
        [InlineData("{\"statsUrl\":\"http://h\",\"startupLockSeconds\":-1}", false)]
        [InlineData("не json", false)]
        public void Settings_value_validation(string value, bool valid)
        {
            Assert.Equal(valid, new CommonSettings().IsValid(value));
        }

        [Fact]
        public async Task Sender_switches_url_without_losing_queue()
        {
            var handler = new UrlHandler();
            using var sender = new StatsSender();
            sender.Start(new ModuleConfig { StatsUrl = "http://old/api/module-events", FlushSeconds = 3600 }, null, handler);
            var stats = new ModuleStats(new ModuleIdentity("m.ext2", null, "1.0.0.0"), sender);

            stats.Use("a");
            sender.Configure(new ModuleConfig { StatsUrl = "http://new/api/module-events", FlushSeconds = 3600 });
            await sender.FlushAsync();

            Assert.Equal(new[] { "http://new/api/module-events" }, handler.Urls);

            sender.Configure(new ModuleConfig { StatsEnabled = false, StatsUrl = "http://new/api/module-events" });
            stats.Use("b");
            await sender.FlushAsync();
            Assert.Single(handler.Urls);
            Assert.Equal(0, sender.Queued);
        }

        [Fact]
        public async Task Sender_keeps_working_configuration_when_update_is_rejected()
        {
            using var handler = new UrlHandler();
            using var sender = new StatsSender();
            sender.Start(new ModuleConfig { StatsUrl = "http://old/api/module-events", FlushSeconds = 3600 }, null, handler);

            Assert.Throws<ArgumentException>(() => sender.Configure(new ModuleConfig
                { StatsUrl = "http://new/api/module-events", FlushSeconds = int.MaxValue }));
            sender.Enqueue(new ModuleEvent());

            Assert.Equal(FlushResult.Accepted, await sender.FlushAsync());
            Assert.Equal("http://old/api/module-events", Assert.Single(handler.Urls));
        }

        [Fact]
        public async Task Out_of_order_notifications_keep_sender_on_latest_configuration()
        {
            ModuleConfig.Reset();
            using var handler = new UrlHandler();
            using var sender = new StatsSender();
            try
            {
                ModuleConfig.Changed += config =>
                {
                    if (config.StatsUrl == "http://old/")
                        ModuleConfig.ApplyCommon("{\"statsUrl\":\"http://new/\"}");
                };
                ModuleConfig.Changed += _ => sender.Configure();
                sender.Start(config: null, env: null, handler: handler);
                ModuleConfig.ApplyCommon("{\"statsUrl\":\"http://old/\"}");
                sender.Enqueue(new ModuleEvent());

                Assert.Equal(FlushResult.Accepted, await sender.FlushAsync());
                Assert.Equal("http://new/", Assert.Single(handler.Urls));
            }
            finally
            {
                ModuleConfig.Reset();
            }
        }

        [Fact]
        public void Editor_writes_json_on_change()
        {
            var provider = new FakeProvider();
            Exception error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var editor = new SettingsEditor(provider);
                    var url = FindTextBoxes(editor)[0];
                    url.Text = "http://stats-new:5310/api/module-events";
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            Assert.Null(error);
            Assert.NotNull(provider.Value);
            Assert.Equal("http://stats-new:5310/api/module-events", ModuleConfig.Parse(provider.Value).StatsUrl);
        }

        private static List<System.Windows.Controls.TextBox> FindTextBoxes(System.Windows.DependencyObject root)
        {
            var result = new List<System.Windows.Controls.TextBox>();
            foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root))
            {
                if (child is System.Windows.Controls.TextBox box)
                    result.Add(box);
                if (child is System.Windows.DependencyObject d)
                    result.AddRange(FindTextBoxes(d));
            }
            return result;
        }

        private sealed class FakeProvider : Ascon.Pilot.SDK.ISettingValueProvider
        {
            public string Value { get; private set; }
            public string GetValue() => Value;
            public void SetValue(string value) => Value = value;
        }

        private sealed class UrlHandler : HttpMessageHandler
        {
            public List<string> Urls { get; } = new List<string>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (Urls)
                    Urls.Add(request.RequestUri.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }
    }
}

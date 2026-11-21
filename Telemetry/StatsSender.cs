using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Pilot.ClientSdk.Infrastructure;

namespace Pilot.ClientSdk.Telemetry
{
    /// <summary>
    /// Отправка событий в сервис сбора (report-metrics-collector, POST /api/module-events).
    /// События копятся в памяти и уходят пачкой раз в flushSeconds (или сразу, если набралось <see cref="BatchSize"/>)
    /// и при закрытии клиента. На диск не пишутся: если сервис недоступен, пачка пишется в лог и теряется.
    /// События, пришедшие до старта Pilot.ClientSdk (модуль загрузился раньше), ждут в очереди.
    /// </summary>
    internal sealed class StatsSender : IDisposable
    {
        public const int BatchSize = 100;
        public const int MaxQueue = 5000;

        private static StatsSender _default = new StatsSender();

        public static StatsSender Default => _default;

        private readonly ConcurrentQueue<ModuleEvent> _queue = new ConcurrentQueue<ModuleEvent>();
        private readonly SemaphoreSlim _flushLock = new SemaphoreSlim(1, 1);
        private readonly object _configLock = new object();
        private volatile ModuleConfig _config;
        private ClientEnvironment _env = new ClientEnvironment();
        private HttpMessageHandler _handler;
        private HttpClient _http;
        private Timer _timer;
        private int _dropped;
        private volatile bool _stopped;

        /// <summary>Отправка настроена (адрес задан, статистика не выключена).</summary>
        public bool Active => _config != null && _config.StatsActive && !_stopped;

        /// <summary>Сервис ещё не настроен, но события принимаются в очередь.</summary>
        public bool Accepting => !_stopped && (_config == null || _config.StatsActive);

        public int Queued => _queue.Count;

        public void Start(ModuleConfig config, ClientEnvironment env, HttpMessageHandler handler = null)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            _env = env ?? new ClientEnvironment();
            _handler = handler;
            Configure(config);
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        /// <summary>
        /// Применить новые настройки (пришли общие настройки Pilot): адрес, ключ, период отправки.
        /// Накопленные события не теряются, если статистика осталась включённой.
        /// </summary>
        public void Configure(ModuleConfig config)
        {
            if (config == null)
                return;
            lock (_configLock)
            {
                if (_stopped)
                    return;
                var validationError = config.ValidationError();
                if (validationError != null)
                    throw new ArgumentException(validationError, nameof(config));

                var old = _config;
                if (old != null && old.StatsActive == config.StatsActive && old.StatsUrl == config.StatsUrl &&
                    old.ApiKey == config.ApiKey && old.FlushSeconds == config.FlushSeconds && _http != null == config.StatsActive)
                {
                    _config = config;
                    return;
                }

                HttpClient http = null;
                Timer timer = null;
                if (config.StatsActive)
                {
                    try
                    {
                        http = _handler == null ? new HttpClient() : new HttpClient(_handler, false);
                        http.Timeout = TimeSpan.FromSeconds(10);
                        if (!string.IsNullOrEmpty(config.ApiKey))
                            http.DefaultRequestHeaders.Add("X-Api-Key", config.ApiKey);
                        var period = TimeSpan.FromSeconds(config.FlushSeconds);
                        timer = new Timer(_ => FlushInBackground(), null, period, period);
                    }
                    catch
                    {
                        http?.Dispose();
                        throw;
                    }
                }

                var oldHttp = _http;
                _timer?.Dispose();
                _timer = timer;
                _http = http;
                _config = config;
                if (!config.StatsActive)
                    while (_queue.TryDequeue(out _)) { }

                // Старый клиент мог быть занят отправкой - освобождаем его чуть позже.
                if (oldHttp != null)
                    Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => oldHttp.Dispose(), TaskScheduler.Default);
            }
        }

        /// <summary>Поставить событие в очередь. Никогда не бросает исключений.</summary>
        public void Enqueue(ModuleEvent e)
        {
            lock (_configLock)
            {
                if (e == null || !Accepting)
                    return;
                _queue.Enqueue(e);
            }
            while (_queue.Count > MaxQueue && _queue.TryDequeue(out _))
                Interlocked.Increment(ref _dropped);
            if (Active && _queue.Count >= BatchSize)
                FlushInBackground();
        }

        public void FlushInBackground()
        {
            if (Active)
                Task.Run(FlushAsync);
        }

        /// <summary>Отправить накопленное. True, если сервис принял хотя бы одну пачку и очередь полностью отправлена.</summary>
        public Task<bool> FlushAsync() => FlushAsync(waitForTurn: false, CancellationToken.None);

        private async Task<bool> FlushAsync(bool waitForTurn, CancellationToken ct)
        {
            if (_config?.StatsActive != true || (!waitForTurn && _stopped))
                return false;
            if (!await _flushLock.WaitAsync(waitForTurn ? Timeout.Infinite : 0, ct).ConfigureAwait(false))
                return false;
            try
            {
                if (!waitForTurn && _stopped)
                    return false;

                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0)
                    ModuleLog.Warn($"Статистика: очередь переполнена, отброшено событий - {dropped}");

                var sentAny = false;
                var allAccepted = true;
                while ((waitForTurn || !_stopped) && !ct.IsCancellationRequested && !_queue.IsEmpty)
                {
                    var batch = new List<ModuleEvent>(BatchSize);
                    while (batch.Count < BatchSize && _queue.TryDequeue(out var e))
                        batch.Add(e);
                    if (batch.Count == 0)
                        break;
                    sentAny = true;
                    var (accepted, sendNext) = await SendAsync(batch, ct).ConfigureAwait(false);
                    allAccepted &= accepted;
                    if (!sendNext)
                        break; // сервис недоступен - не долбим его остатком очереди
                }
                return sentAny && allAccepted && _queue.IsEmpty;
            }
            finally
            {
                _flushLock.Release();
            }
        }

        private async Task<(bool Accepted, bool SendNext)> SendAsync(List<ModuleEvent> batch, CancellationToken ct)
        {
            var sb = new StringBuilder(batch.Count * 400).Append('[');
            foreach (var e in batch)
            {
                _env.Fill(e);
                sb.Append(e.ToJson()).Append(',');
            }
            sb.Length--;
            sb.Append(']');

            HttpClient http;
            string url;
            lock (_configLock)
            {
                http = _http;
                url = _config?.StatsUrl;
            }
            if (http == null || url == null)
                return (false, false);

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");
                    // По этому времени сервис поправит ts, если часы на рабочем месте сбиты.
                    request.Headers.TryAddWithoutValidation("X-Client-Time", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    using (var response = await http.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        if (response.IsSuccessStatusCode)
                            return (true, true);
                        ModuleLog.Warn($"Статистика ({batch.Count} событий) не принята: {(int)response.StatusCode} {response.ReasonPhrase}");
                        return (false, (int)response.StatusCode < 500); // при 4xx отправляем остальное
                    }
                }
            }
            catch (Exception ex)
            {
                ModuleLog.Warn($"Статистика ({batch.Count} событий) не отправлена ({url}): {ex.GetBaseException().Message}");
                return (false, false);
            }
        }

        /// <summary>Остановка при закрытии клиента: последняя отправка с ограничением по времени.</summary>
        public void Stop(TimeSpan wait)
        {
            lock (_configLock)
            {
                if (_stopped)
                    return;
                _stopped = true;
                _timer?.Dispose();
            }
            try
            {
                using (var deadline = new CancellationTokenSource(wait))
                {
                    if (!FlushAsync(waitForTurn: true, deadline.Token).Wait(wait))
                        deadline.Cancel();
                }
            }
            catch (AggregateException ex) when (ex.GetBaseException() is OperationCanceledException)
            {
                ModuleLog.Info("Статистика: время ожидания при завершении клиента вышло");
            }
            catch (Exception ex)
            {
                ModuleLog.Warn("Не удалось завершить отправку статистики перед закрытием клиента", ex);
            }
        }

        private void OnProcessExit(object sender, EventArgs e) => Stop(TimeSpan.FromSeconds(3));

        public void Dispose()
        {
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            lock (_configLock)
            {
                _stopped = true;
                _timer?.Dispose();
                _http?.Dispose();
            }
        }

        /// <summary>Для тестов: новый отправитель по умолчанию.</summary>
        internal static StatsSender ResetDefault()
        {
            _default?.Dispose();
            _default = new StatsSender();
            return _default;
        }
    }
}

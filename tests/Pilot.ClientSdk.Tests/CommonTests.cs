using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Threading;
using System.Threading.Tasks;
using Ascon.Pilot.SDK;
using Pilot.ClientSdk;
using Pilot.ClientSdk.Infrastructure;
using Xunit;

namespace Pilot.ClientSdk.Tests
{
    // DispatcherOverride - статическое поле: тест с диспетчером не должен идти параллельно с другими.
    [CollectionDefinition("SdkDispatcher", DisableParallelization = true)]
    public class SdkDispatcherCollection { }

    [Collection("SdkDispatcher")]
    public class CommonTests
    {
        [Theory]
        [InlineData("Иванов Иван Иванович", "И.И. Иванов", "Иванов И.И.")]
        [InlineData("Петров пётр", "П. Петров", "Петров П.")]
        [InlineData("Отдел", "Отдел", "Отдел")]
        public void Fio_and_short_name(string full, string fio, string shortName)
        {
            Assert.Equal(fio, Text.Fio(full));
            Assert.Equal(shortName, Text.ShortName(full));
        }

        [Fact]
        public void Codes_sort_numerically()
        {
            var codes = new[] { "1.10", "1.2", "1", "1.2.1", "" };
            Assert.Equal(new[] { "", "1", "1.2", "1.2.1", "1.10" }, codes.OrderBy(c => c, Text.CodeComparer));
        }

        [Fact]
        public void Codes_sort_mixed_parts_consistently()
        {
            Assert.True(Text.CompareCodes("2", "10") < 0);
            Assert.True(Text.CompareCodes("10", "15a") < 0);
            Assert.True(Text.CompareCodes("2", "15a") < 0);
            Assert.True(Text.CompareCodes("15a", "2") > 0);
        }

        [Fact]
        public void Integer_attributes_check_range_after_truncation()
        {
            var values = new Dictionary<string, object>
            {
                ["decimalOverflow"] = decimal.MaxValue,
                ["doubleOverflow"] = (double)long.MaxValue,
                ["nan"] = double.NaN,
                ["largest"] = (decimal)long.MaxValue + 0.5m,
                ["fraction"] = -12.75d,
            };
            var obj = new SdkStub<IDataObject>(new Dictionary<string, object> { ["get_Attributes"] = values }).Object;

            Assert.Null(obj.Int("decimalOverflow"));
            Assert.Null(obj.Int("doubleOverflow"));
            Assert.Null(obj.Int("nan"));
            Assert.Equal(long.MaxValue, obj.Int("largest"));
            Assert.Equal(-12L, obj.Int("fraction"));
        }

        [Theory]
        [InlineData(DataState.Loaded, 1)]
        [InlineData(DataState.NonExistent, 0)]
        public async Task Loader_waits_for_final_state_after_cache_miss(DataState finalState, int expectedCount)
        {
            var id = Guid.NewGuid();
            var source = new ManualSource<IDataObject>();
            var repository = new SdkStub<IObjectsRepository>(new Dictionary<string, object> { ["SubscribeObjects"] = source }).Object;
            IDataObject WithState(DataState state) => new SdkStub<IDataObject>(new Dictionary<string, object>
                { ["get_Id"] = id, ["get_State"] = state }).Object;

            var loaded = new Loader(repository).LoadAsync(new[] { id }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            source.Push(WithState(DataState.NoData));
            Assert.False(loaded.IsCompleted);

            source.Push(WithState(finalState));
            Assert.Equal(expectedCount, (await loaded).Count);
            Assert.True(source.Disposed);
        }

        [Fact]
        public void Clean_title_removes_quotes()
        {
            Assert.Equal("ООО Институт Мосинжпроект", Text.CleanTitle(" ООО «Институт \"Мосинжпроект\"» "));
        }

        [Fact]
        public async Task Subscription_returns_result_when_complete()
        {
            var source = new ManualSource<int>();
            var seen = new List<int>();
            var task = SdkSubscription.RunAsync<int, List<int>>(source.Subscribe, i => { seen.Add(i); return seen.Count == 3; },
                () => seen.ToList(), TimeSpan.FromSeconds(5), CancellationToken.None, "test");

            source.Push(1);
            source.Push(2);
            Assert.False(task.IsCompleted);
            source.Push(3);

            Assert.Equal(new[] { 1, 2, 3 }, await task);
            Assert.True(source.Disposed);
        }

        [Fact]
        public async Task Subscription_is_disposed_before_result_is_visible()
        {
            // Раньше результат выставлялся до снятия подписки: медленный Dispose был ещё не закончен, когда код после await уже шёл дальше.
            var source = new ManualSource<int> { DisposeDelay = TimeSpan.FromMilliseconds(150) };
            var task = SdkSubscription.RunAsync<int, int>(source.Subscribe, _ => true, () => 1, TimeSpan.FromSeconds(5), CancellationToken.None, "test");
            _ = Task.Run(() => source.Push(1), TestContext.Current.CancellationToken);

            Assert.Equal(1, await task);
            Assert.True(source.Disposed);
        }

        [Fact]
        public async Task Subscription_with_dispatcher_is_disposed_on_ui_thread_before_result()
        {
            // Боевой путь: диспетчер есть, Finish вызван с не-UI потока. Снятие подписки не должно уйти в BeginInvoke.
            var started = new ManualResetEventSlim();
            System.Windows.Threading.Dispatcher dispatcher = null;
            var uiThread = new Thread(() =>
            {
                dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                started.Set();
                System.Windows.Threading.Dispatcher.Run();
            });
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
            started.Wait(TestContext.Current.CancellationToken);
            SdkSubscription.DispatcherOverride = dispatcher;
            try
            {
                var source = new ManualSource<int> { DisposeDelay = TimeSpan.FromMilliseconds(150) };
                var task = SdkSubscription.RunAsync<int, int>(source.Subscribe, _ => true, () => 1, TimeSpan.FromSeconds(5), CancellationToken.None, "test");
                _ = Task.Run(() => source.Push(1), TestContext.Current.CancellationToken);

                Assert.Equal(1, await task);
                Assert.True(source.Disposed);
                Assert.Equal(uiThread.ManagedThreadId, source.DisposedOnThread);
            }
            finally
            {
                SdkSubscription.DispatcherOverride = null;
                dispatcher.InvokeShutdown();
                uiThread.Join();
            }
        }

        [Theory]
        [InlineData("<Config><Item>первый</Item><Item>второй</Item></Config>", 0, "первый")]
        [InlineData("<Config><Item>первый</Item><Item>второй</Item></Config>", 1, "второй")]
        [InlineData("<Config><Item>первый</Item></Config>", 5, "")]
        [InlineData("<Config><Item>первый</Item></Config>", -1, "")]
        [InlineData("<Config><Item>не закрыт", 0, "")]
        [InlineData("<!DOCTYPE c [<!ENTITY e \"x\">]><Config><Item>&e;</Item></Config>", 0, "")] // DTD запрещён
        public void Const_attr_item_text(string config, int index, string expected)
        {
            Assert.Equal(expected, TypeExtensions.ItemText(config, index));
        }

        [Fact]
        public async Task Subscription_timeout_returns_partial_result()
        {
            var source = new ManualSource<int>();
            var seen = new List<int>();
            var task = SdkSubscription.RunAsync<int, List<int>>(source.Subscribe, i => { seen.Add(i); return false; },
                () => seen.ToList(), TimeSpan.FromMilliseconds(200), CancellationToken.None, "test");
            source.Push(7);

            Assert.Equal(new[] { 7 }, await task);
            Assert.True(source.Disposed);
        }

        [Fact]
        public async Task Subscription_error_and_cancel()
        {
            var failing = new ManualSource<int>();
            var t1 = SdkSubscription.RunAsync<int, int>(failing.Subscribe, _ => false, () => 0, TimeSpan.FromSeconds(5), CancellationToken.None, "test");
            failing.Fail(new InvalidOperationException("boom"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => t1);

            var cts = new CancellationTokenSource();
            var t2 = SdkSubscription.RunAsync<int, int>(new ManualSource<int>().Subscribe, _ => false, () => 0, TimeSpan.FromSeconds(5), cts.Token, "test");
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t2);
        }

        [Fact]
        public async Task Subscription_surfaces_callback_error_and_disposes_subscription()
        {
            var source = new ManualSource<int>();
            var expected = new InvalidOperationException("predicate failed");
            var task = SdkSubscription.RunAsync<int, int>(source.Subscribe, _ => throw expected, () => 0,
                TimeSpan.FromSeconds(5), CancellationToken.None, "test");

            source.Push(1);

            Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => task));
            Assert.True(source.Disposed);
        }

        [Fact]
        public async Task Subscription_does_not_start_when_already_cancelled()
        {
            var subscribed = false;
            var task = SdkSubscription.RunAsync<int, int>(_ => { subscribed = true; return null; },
                _ => false, () => 0, TimeSpan.FromSeconds(5), new CancellationToken(true), "test");

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.False(subscribed);
        }

        [Fact]
        public void Sync_wait_off_ui_thread()
        {
            var source = new ManualSource<int>();
            var task = SdkSubscription.RunAsync<int, int>(source.Subscribe, i => true, () => 5, TimeSpan.FromSeconds(5), CancellationToken.None, "test");
            Task.Run(() => source.Push(1), TestContext.Current.CancellationToken);
            Assert.Equal(5, SdkSubscription.Wait(task));
        }

        [Fact]
        public void Org_tree_parent_department_positions_contragent()
        {
            //  1 ООО «Ромашка» / 2 Отдел АР / 3 Главный архитектор (должность)
            //                               / 4 Сектор / 5 Архитектор (должность)
            var org = new Org(new IOrganisationUnit[]
            {
                new Unit(1, "ООО «Ромашка»", false, 2),
                new Unit(2, "Отдел АР", false, 3, 4),
                new Unit(3, "Главный архитектор", true),
                new Unit(4, "Сектор", false, 5),
                new Unit(5, "Архитектор", true),
            });

            Assert.Equal(4, org.Parent(5).Id);
            Assert.Equal(new[] { 4, 2, 1 }, org.Ancestors(5).Select(u => u.Id));
            Assert.Equal(4, org.DepartmentOf(5).Id);
            Assert.Equal(2, org.DepartmentOf(2).Id);
            Assert.Equal(new[] { 3, 5 }, org.PositionIds(2).OrderBy(x => x));
            Assert.Equal(1, org.ContrAgent(5).Id);
            Assert.Equal(2, org.UnitByTitle("отдел ар").Id);
            Assert.Equal("ООО «Ромашка» / Отдел АР / Сектор / Архитектор", org.Path(5));
            Assert.Null(org.Parent(1));
        }

        private sealed class SdkStub<T> : RealProxy where T : class
        {
            private readonly IReadOnlyDictionary<string, object> _responses;

            public SdkStub(IReadOnlyDictionary<string, object> responses) : base(typeof(T)) => _responses = responses;

            public T Object => (T)GetTransparentProxy();

            public override IMessage Invoke(IMessage message)
            {
                var call = (IMethodCallMessage)message;
                return new ReturnMessage(_responses[call.MethodName], null, 0, call.LogicalCallContext, call);
            }
        }

        private sealed class ManualSource<T> : IObservable<T>
        {
            private IObserver<T> _observer;
            public bool Disposed { get; private set; }
            public int DisposedOnThread { get; private set; }
            public TimeSpan DisposeDelay { get; set; }

            public IDisposable Subscribe(IObserver<T> observer)
            {
                _observer = observer;
                return new Unsubscriber(() => { Thread.Sleep(DisposeDelay); DisposedOnThread = Thread.CurrentThread.ManagedThreadId; Disposed = true; });
            }

            public void Push(T value) => _observer.OnNext(value);
            public void Fail(Exception ex) => _observer.OnError(ex);

            private sealed class Unsubscriber : IDisposable
            {
                private readonly Action _dispose;
                public Unsubscriber(Action dispose) => _dispose = dispose;
                public void Dispose() => _dispose();
            }
        }

        private sealed class Unit : IOrganisationUnit
        {
            public Unit(int id, string title, bool isPosition, params int[] children)
            {
                Id = id;
                Title = title;
                IsPosition = isPosition;
                Children = new System.Collections.ObjectModel.ReadOnlyCollection<int>(children);
            }

            public int Id { get; }
            public string Title { get; }
            public bool IsDeleted => false;
            public System.Collections.ObjectModel.ReadOnlyCollection<int> Children { get; }
            public bool IsPosition { get; }
            public bool IsChief => false;
        }
    }
}

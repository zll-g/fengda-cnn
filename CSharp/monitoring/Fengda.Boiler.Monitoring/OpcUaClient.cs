using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Globalization;
using System.Xml;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using Opc.Ua.Security.Certificates;

namespace Fengda.Boiler.Monitoring;

/// <summary>
/// OPC UA 客户端应用配置工厂。
/// </summary>
public static class OpcUaApplicationFactory
{
    /// <summary>
    /// 创建 OPC UA 客户端配置并初始化应用证书。
    /// </summary>
    public static async Task<ApplicationConfiguration> BuildAsync(
        BoilerMonitorOptions options,
        IMonitorLogger logger,
        CancellationToken cancellationToken = default)
    {
        var pkiRoot = Path.Combine(Path.GetFullPath(options.OutputDirectory), "pki");
        Directory.CreateDirectory(Path.Combine(pkiRoot, "own"));
        Directory.CreateDirectory(Path.Combine(pkiRoot, "trusted"));
        Directory.CreateDirectory(Path.Combine(pkiRoot, "issuer"));
        Directory.CreateDirectory(Path.Combine(pkiRoot, "rejected"));

        var configuration = new ApplicationConfiguration
        {
            ApplicationName = "FengdaBoilerMonitor",
            ApplicationUri = Utils.Format(@"urn:{0}:FengdaBoilerMonitor", Dns.GetHostName()),
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "own"),
                    SubjectName = $"CN=FengdaBoilerMonitor, DC={Dns.GetHostName()}"
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "trusted")
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "issuer")
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "rejected")
                },
                AutoAcceptUntrustedCertificates = true,
                AddAppCertToTrustedStore = false
            },
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = 15000,
                MaxStringLength = 4 * 1024 * 1024,
                MaxByteStringLength = 4 * 1024 * 1024,
                MaxArrayLength = 65535,
                MaxMessageSize = 4 * 1024 * 1024,
                MaxBufferSize = 4 * 1024 * 1024,
                ChannelLifetime = 300000,
                SecurityTokenLifetime = 3600000
            },
            ClientConfiguration = new ClientConfiguration
            {
                DefaultSessionTimeout = 60000
            },
            DisableHiResClock = true
        };

        await configuration.ValidateAsync(ApplicationType.Client, cancellationToken).ConfigureAwait(false);
        configuration.CertificateValidator.CertificateValidation += (_, eventArgs) =>
        {
            if (eventArgs.Error.StatusCode == StatusCodes.BadCertificateUntrusted)
            {
                logger.Warn($"自动接受不受信任的服务端证书：{eventArgs.Certificate.Subject}");
                eventArgs.Accept = true;
            }
        };

        await EnsureApplicationCertificateAsync(configuration, logger, cancellationToken).ConfigureAwait(false);
        return configuration;
    }

    private static async Task EnsureApplicationCertificateAsync(
        ApplicationConfiguration configuration,
        IMonitorLogger logger,
        CancellationToken cancellationToken)
    {
        var certificateIdentifier = configuration.SecurityConfiguration.ApplicationCertificate
            ?? throw new InvalidOperationException("未配置 OPC UA 客户端应用证书。");

        var certificate = await certificateIdentifier
            .FindAsync(true, configuration.ApplicationUri, null, cancellationToken)
            .ConfigureAwait(false);

        if (certificate is null)
        {
            logger.Info("正在创建 OPC UA 客户端自签名证书。");
            certificate = CertificateFactory.CreateCertificate(
                    configuration.ApplicationUri,
                    configuration.ApplicationName,
                    certificateIdentifier.SubjectName,
                    [Dns.GetHostName(), "localhost"])
                .CreateForRSA();

            await X509Utils.AddToStoreAsync(
                    certificate,
                    certificateIdentifier.StoreType,
                    certificateIdentifier.StorePath,
                    null,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        certificateIdentifier.Certificate = certificate;
    }
}

/// <summary>
/// OPC UA 匿名会话连接工厂。
/// </summary>
public static class OpcUaSessionFactory
{
    /// <summary>
    /// 发现并连接支持匿名登录的 OPC UA 终端。
    /// </summary>
    public static async Task<Session> ConnectAsync(
        ApplicationConfiguration configuration,
        string endpointUrl,
        IMonitorLogger logger,
        CancellationToken cancellationToken = default)
    {
        var endpoints = await DiscoverEndpointsAsync(configuration, endpointUrl, logger, cancellationToken)
            .ConfigureAwait(false);
        if (endpoints.Count == 0)
        {
            logger.Warn("终端发现未返回结果，尝试直接选择 OPC UA 终端。");
            var selectedEndpoint = await CoreClientUtils
                .SelectEndpointAsync(configuration, endpointUrl, false, 15000, null!, cancellationToken)
                .ConfigureAwait(false);
            if (selectedEndpoint is null)
            {
                throw new InvalidOperationException($"OPC UA 服务端未返回可用终端：{endpointUrl}");
            }

            endpoints.Add(selectedEndpoint);
        }

        Exception? lastException = null;
        foreach (var endpoint in endpoints
                     .Where(SupportsAnonymous)
                     .OrderByDescending(GetSecurityRank)
                     .ThenByDescending(item => item.SecurityLevel))
        {
            try
            {
                var anonymousPolicy = endpoint.UserIdentityTokens
                    .First(token => token.TokenType == UserTokenType.Anonymous);
                logger.Info(
                    $"正在连接 OPC UA 终端：Url={endpoint.EndpointUrl}, SecurityMode={endpoint.SecurityMode}, SecurityPolicy={endpoint.SecurityPolicyUri}");

                var configuredEndpoint = new ConfiguredEndpoint(
                    null,
                    endpoint,
                    EndpointConfiguration.Create(configuration));
                var identity = new UserIdentity(new AnonymousIdentityToken
                {
                    PolicyId = anonymousPolicy.PolicyId
                });
                var sessionFactory = new DefaultSessionFactory(null!);
                var createdSession = await sessionFactory.CreateAsync(
                        configuration,
                        configuredEndpoint,
                        false,
                        false,
                        configuration.ApplicationName,
                        60000,
                        identity,
                        null,
                        cancellationToken)
                    .ConfigureAwait(false);

                var session = (Session)createdSession;
                logger.Info($"OPC UA 连接成功：SessionId={session.SessionId}");
                return session;
            }
            catch (Exception ex)
            {
                lastException = ex;
                logger.Warn($"OPC UA 终端连接失败：Url={endpoint.EndpointUrl}, Error={ex.Message}");
            }
        }

        throw new InvalidOperationException($"无法匿名连接 OPC UA 服务端：{endpointUrl}", lastException);
    }

    private static async Task<List<EndpointDescription>> DiscoverEndpointsAsync(
        ApplicationConfiguration configuration,
        string endpointUrl,
        IMonitorLogger logger,
        CancellationToken cancellationToken)
    {
        var discoveryUrls = new[]
            {
                endpointUrl,
                endpointUrl.EndsWith("/discovery", StringComparison.OrdinalIgnoreCase)
                    ? endpointUrl
                    : $"{endpointUrl.TrimEnd('/')}/discovery"
            }
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var discoveryUrl in discoveryUrls)
        {
            try
            {
                using var discoveryClient = await DiscoveryClient
                    .CreateAsync(configuration, new Uri(discoveryUrl), DiagnosticsMasks.None, cancellationToken)
                    .ConfigureAwait(false);
                var endpoints = await discoveryClient
                    .GetEndpointsAsync(null, cancellationToken)
                    .ConfigureAwait(false);
                logger.Info($"OPC UA 终端发现完成：Count={endpoints.Count}, Url={discoveryUrl}");
                return endpoints.ToList();
            }
            catch (Exception ex)
            {
                logger.Warn($"OPC UA 终端发现失败：Url={discoveryUrl}, Error={ex.Message}");
            }
        }

        return [];
    }

    private static bool SupportsAnonymous(EndpointDescription endpoint)
    {
        return endpoint.UserIdentityTokens?.Any(token => token.TokenType == UserTokenType.Anonymous) == true;
    }

    private static int GetSecurityRank(EndpointDescription endpoint)
    {
        return endpoint.SecurityMode switch
        {
            MessageSecurityMode.SignAndEncrypt => 3,
            MessageSecurityMode.Sign => 2,
            _ => 1
        };
    }
}

/// <summary>
/// OPC UA 锅炉持续监控器。
/// </summary>
public sealed class OpcUaMonitor : IAsyncDisposable
{
    private const int ReadBatchSize = 100;
    private const string BlowerCurrentNodeId = "ns=1;s=GYDCS.K0QHL10AN001I";
    private const string BlowerCurrentDisplayName = "K0QHL10AN001I(鼓风机变频器电流)";
    private const double BlowerCurrentThreshold = 10d;

    private readonly object syncRoot = new();
    private readonly ApplicationConfiguration configuration;
    private readonly BoilerMonitorOptions options;
    private readonly IMonitorLogger logger;
    private readonly IBlowerCurrentEventLogger? blowerEventLogger;
    private readonly BoilerSnapshotStore snapshotStore;
    private readonly BoilerSnapshotCache snapshotCache;
    private readonly IReadOnlyList<BoilerPointBinding> points = BoilerPointBindings.All;
    private readonly BoilerSnapshot currentSnapshot = new();

    private Session? session;
    private Subscription? subscription;
    private TaskCompletionSource<bool>? reconnectSignal;
    private MonitorConnectionState connectionState = MonitorConnectionState.Starting;
    private int reconnectRequested;
    private int hasSeedData;
    private double? lastBlowerCurrentValue;

    /// <summary>
    /// 创建 OPC UA 锅炉持续监控器。
    /// </summary>
    public OpcUaMonitor(
        ApplicationConfiguration configuration,
        BoilerMonitorOptions options,
        IMonitorLogger logger,
        BoilerSnapshotStore snapshotStore,
        BoilerSnapshotCache snapshotCache,
        IBlowerCurrentEventLogger? blowerEventLogger = null)
    {
        this.configuration = configuration;
        this.options = options;
        this.logger = logger;
        this.snapshotStore = snapshotStore;
        this.snapshotCache = snapshotCache;
        this.blowerEventLogger = blowerEventLogger;
    }

    /// <summary>
    /// 当前 OPC UA 连接状态。
    /// </summary>
    public MonitorConnectionState ConnectionState
    {
        get
        {
            lock (syncRoot)
            {
                return connectionState;
            }
        }
    }

    /// <summary>
    /// 启动连接、订阅和每秒快照保存循环。
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(
                SnapshotLoopAsync(cancellationToken),
                ConnectionLoopAsync(cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CleanupSessionAsync().ConfigureAwait(false);
    }

    private async Task ConnectionLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SetConnectionState(MonitorConnectionState.Connecting, "正在连接 OPC UA 服务端。");
                var newSession = await OpcUaSessionFactory
                    .ConnectAsync(configuration, options.EndpointUrl, logger, cancellationToken)
                    .ConfigureAwait(false);
                newSession.KeepAlive += SessionKeepAlive;

                lock (syncRoot)
                {
                    session = newSession;
                    reconnectSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                Interlocked.Exchange(ref reconnectRequested, 0);
                logger.Info("正在执行全部点位初始读取。");
                await FullReadAsync(newSession, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref hasSeedData, 1);

                logger.Info("正在创建 OPC UA 点位订阅。");
                var newSubscription = await CreateSubscriptionAsync(newSession, cancellationToken).ConfigureAwait(false);
                lock (syncRoot)
                {
                    subscription = newSubscription;
                }

                SetConnectionState(MonitorConnectionState.Connected, $"OPC UA 监听已启动，点位数：{points.Count}。");
                Task reconnectTask;
                lock (syncRoot)
                {
                    reconnectTask = reconnectSignal?.Task
                        ?? Task.FromException(new InvalidOperationException("未初始化断线重连信号。"));
                }

                using var registration = cancellationToken.Register(RequestStop);
                await reconnectTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.Error("OPC UA 监控周期执行失败。", ex);
            }
            finally
            {
                await CleanupSessionAsync().ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            SetConnectionState(
                MonitorConnectionState.Reconnecting,
                $"{options.ReconnectDelay.TotalSeconds:0.###} 秒后重新连接。");
            await Task.Delay(options.ReconnectDelay, cancellationToken).ConfigureAwait(false);
        }

        SetConnectionState(MonitorConnectionState.Stopped, "监控程序已停止。");
    }

    private async Task SnapshotLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.SnapshotInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (Volatile.Read(ref hasSeedData) == 0)
                {
                    continue;
                }

                var snapshot = CaptureSnapshot(BoilerTime.ChinaNow);
                await snapshotStore.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
                snapshotCache.AddOrUpdate(snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.Error("保存锅炉每秒快照失败。", ex);
            }
        }
    }

    private BoilerSnapshot CaptureSnapshot(DateTime recordedAt)
    {
        lock (syncRoot)
        {
            var snapshot = currentSnapshot.Copy();
            snapshot.RecordedAt = recordedAt;
            snapshot.ConnectionState = connectionState.ToString();
            return snapshot;
        }
    }

    private async Task FullReadAsync(Session currentSession, CancellationToken cancellationToken)
    {
        var badStatusCount = 0;
        for (var offset = 0; offset < points.Count; offset += ReadBatchSize)
        {
            var batch = points.Skip(offset).Take(ReadBatchSize).ToList();
            var nodesToRead = new ReadValueIdCollection();
            foreach (var point in batch)
            {
                nodesToRead.Add(new ReadValueId
                {
                    NodeId = NodeId.Parse(point.NodeId),
                    AttributeId = Attributes.Value
                });
            }

            var response = await currentSession.ReadAsync(
                    null,
                    0,
                    TimestampsToReturn.Both,
                    nodesToRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.Results is null || response.Results.Count != batch.Count)
            {
                throw new InvalidOperationException("OPC UA 服务端初始读取返回的点位数量不正确。");
            }

            for (var index = 0; index < batch.Count; index++)
            {
                UpdatePointValue(batch[index], response.Results[index]);
                if (StatusCode.IsBad(response.Results[index].StatusCode))
                {
                    badStatusCount++;
                }
            }
        }

        logger.Info($"全部点位初始读取完成，异常状态点位数：{badStatusCount}。");
    }

    private async Task<Subscription> CreateSubscriptionAsync(Session currentSession, CancellationToken cancellationToken)
    {
        var newSubscription = new Subscription(currentSession.DefaultSubscription)
        {
            DisplayName = "FengdaBoilerMonitor",
            PublishingInterval = (int)options.SnapshotInterval.TotalMilliseconds,
            PublishingEnabled = true,
            KeepAliveCount = 10,
            LifetimeCount = 30,
            Priority = 100
        };

        foreach (var point in points)
        {
            var monitoredItem = new MonitoredItem(newSubscription.DefaultItem)
            {
                StartNodeId = NodeId.Parse(point.NodeId),
                AttributeId = Attributes.Value,
                DisplayName = point.ColumnName,
                SamplingInterval = (int)options.SnapshotInterval.TotalMilliseconds,
                QueueSize = 1,
                DiscardOldest = true,
                MonitoringMode = MonitoringMode.Reporting,
                Handle = point
            };
            monitoredItem.Notification += OnMonitoredItemNotification;
            newSubscription.AddItem(monitoredItem);
        }

        currentSession.AddSubscription(newSubscription);
        await newSubscription.CreateAsync(cancellationToken).ConfigureAwait(false);
        await newSubscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.Info($"OPC UA 订阅创建成功，监听点位数：{points.Count}。");
        return newSubscription;
    }

    private void OnMonitoredItemNotification(MonitoredItem monitoredItem, MonitoredItemNotificationEventArgs eventArgs)
    {
        try
        {
            if (monitoredItem.Handle is not BoilerPointBinding point)
            {
                logger.Warn($"OPC UA 订阅项缺少点位绑定：{monitoredItem.DisplayName}");
                return;
            }

            foreach (var dataValue in monitoredItem.DequeueValues())
            {
                UpdatePointValue(point, dataValue);
            }

            Volatile.Write(ref hasSeedData, 1);
        }
        catch (Exception ex)
        {
            logger.Error($"处理 OPC UA 点位通知失败：{monitoredItem.StartNodeId}", ex);
        }
    }

    private void UpdatePointValue(BoilerPointBinding point, DataValue dataValue)
    {
        var hasNumericValue = OpcUaValueFormatter.TryFormatNumericValue(
            dataValue.WrappedValue.Value,
            out var numericValue);
        BlowerCurrentTransition? transition = null;
        lock (syncRoot)
        {
            point.SetValue(currentSnapshot, hasNumericValue ? numericValue : null);
            if (string.Equals(point.NodeId, BlowerCurrentNodeId, StringComparison.Ordinal)
                && hasNumericValue)
            {
                transition = EvaluateBlowerCurrentTransition(numericValue);
            }
        }

        if (transition is not null)
        {
            blowerEventLogger?.Log(
                transition.EventType,
                BlowerCurrentDisplayName,
                transition.PreviousValue,
                transition.CurrentValue,
                BlowerCurrentThreshold);
        }
    }

    private BlowerCurrentTransition? EvaluateBlowerCurrentTransition(double currentValue)
    {
        if (lastBlowerCurrentValue is null)
        {
            lastBlowerCurrentValue = currentValue;
            return null;
        }

        var previousValue = lastBlowerCurrentValue.Value;
        lastBlowerCurrentValue = currentValue;
        if (previousValue < BlowerCurrentThreshold && currentValue > BlowerCurrentThreshold)
        {
            return new BlowerCurrentTransition("启动", previousValue, currentValue);
        }

        if (previousValue > BlowerCurrentThreshold && currentValue < BlowerCurrentThreshold)
        {
            return new BlowerCurrentTransition("停机", previousValue, currentValue);
        }

        return null;
    }

    private void SessionKeepAlive(ISession currentSession, KeepAliveEventArgs eventArgs)
    {
        if (ServiceResult.IsBad(eventArgs.Status))
        {
            RequestReconnect($"KeepAlive 状态异常：{eventArgs.Status}");
        }
    }

    private void RequestReconnect(string reason)
    {
        if (Interlocked.Exchange(ref reconnectRequested, 1) != 0)
        {
            return;
        }

        SetConnectionState(MonitorConnectionState.Reconnecting, reason);
        lock (syncRoot)
        {
            reconnectSignal?.TrySetResult(true);
        }
    }

    private void RequestStop()
    {
        lock (syncRoot)
        {
            reconnectSignal?.TrySetCanceled();
        }
    }

    private async Task CleanupSessionAsync()
    {
        Session? currentSession;
        Subscription? currentSubscription;
        lock (syncRoot)
        {
            currentSession = session;
            currentSubscription = subscription;
            session = null;
            subscription = null;
            reconnectSignal = null;
        }

        if (currentSubscription is not null)
        {
            try
            {
                foreach (var monitoredItem in currentSubscription.MonitoredItems)
                {
                    monitoredItem.Notification -= OnMonitoredItemNotification;
                }

                await currentSubscription.DeleteAsync(true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Warn($"清理 OPC UA 订阅失败：{ex.Message}");
            }
        }

        if (currentSession is null)
        {
            return;
        }

        currentSession.KeepAlive -= SessionKeepAlive;
        try
        {
            await currentSession.CloseAsync(5000, true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Warn($"关闭 OPC UA 会话失败：{ex.Message}");
        }

        currentSession.Dispose();
    }

    private void SetConnectionState(MonitorConnectionState state, string message)
    {
        lock (syncRoot)
        {
            connectionState = state;
        }

        logger.Info($"连接状态变更为 {state}：{message}");
    }
}

/// <summary>
/// OPC UA 监控连接状态。
/// </summary>
public enum MonitorConnectionState
{
    /// <summary>正在启动。</summary>
    Starting,
    /// <summary>正在连接。</summary>
    Connecting,
    /// <summary>已连接。</summary>
    Connected,
    /// <summary>正在重连。</summary>
    Reconnecting,
    /// <summary>已停止。</summary>
    Stopped
}

internal static class OpcUaValueFormatter
{
    public static string FormatValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            LocalizedText localizedText => localizedText.Text ?? string.Empty,
            QualifiedName qualifiedName => qualifiedName.ToString(),
            byte[] bytes => Convert.ToBase64String(bytes),
            XmlElement xmlElement => xmlElement.OuterXml,
            DateTime dateTime => dateTime == DateTime.MinValue ? string.Empty : dateTime.ToString("O"),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O"),
            Array array => string.Join("|", array.Cast<object?>().Select(FormatValue)),
            ExtensionObject extensionObject => extensionObject.Body?.ToString() ?? string.Empty,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    public static bool TryFormatNumericValue(object? value, out double numericValue)
    {
        try
        {
            if (value is not null && value is IConvertible convertible)
            {
                numericValue = convertible.ToDouble(CultureInfo.InvariantCulture);
                return double.IsFinite(numericValue);
            }
        }
        catch (Exception)
        {
            // OPC UA 非数值类型按无法转换处理。
        }

        numericValue = 0;
        return false;
    }
}

internal sealed record BlowerCurrentTransition(
    string EventType,
    double PreviousValue,
    double CurrentValue);

using System.Security.Cryptography;
using System.Text;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;
using Renci.SshNet;

namespace Badger.SqlMigrate.Connectivity;

/// <summary>
/// Local port forward through an SSH bastion. Vendor-neutral: the provider just receives 127.0.0.1:&lt;port&gt;
/// as an endpoint override. Host keys are verified against known_hosts (or a pinned fingerprint).
/// </summary>
public sealed class SshTunnel : IDisposable
{
    private readonly SshClient client;
    private readonly ForwardedPortLocal forward;

    private SshTunnel(SshClient client, ForwardedPortLocal forward)
    {
        this.client = client;
        this.forward = forward;
    }

    public Endpoint LocalEndpoint => new("127.0.0.1", (int)forward.BoundPort);

    public static SshTunnel Open(SshOptions options, Endpoint target, Action<string>? warn = null)
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(options.PrivateKeyFile))
        {
            var keyPath = ExpandHome(options.PrivateKeyFile);
            var key = string.IsNullOrEmpty(options.PrivateKeyPassphrase) ? new PrivateKeyFile(keyPath) : new PrivateKeyFile(keyPath, options.PrivateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(options.User, key));
        }

        if (!string.IsNullOrEmpty(options.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(options.User, options.Password));
        }

        if (methods.Count == 0)
        {
            throw new MigrationException("ssh: set 'privateKeyFile' or 'password' for the tunnel.");
        }

        var client = new SshClient(new ConnectionInfo(options.Host, options.Port, options.User, [.. methods]));
        client.HostKeyReceived += (_, e) =>
        {
            if (options.InsecureSkipHostKeyCheck)
            {
                warn?.Invoke($"ssh: host key verification for {options.Host} is disabled (insecureSkipHostKeyCheck).");
                e.CanTrust = true;
                return;
            }

            if (!string.IsNullOrEmpty(options.HostKeyFingerprint))
            {
                var expected = options.HostKeyFingerprint.StartsWith("SHA256:", StringComparison.Ordinal) ? options.HostKeyFingerprint[7..] : options.HostKeyFingerprint;
                e.CanTrust = string.Equals(expected.TrimEnd('='), e.FingerPrintSHA256.TrimEnd('='), StringComparison.Ordinal);
                return;
            }

            var knownHosts = ExpandHome(options.KnownHostsFile ?? Path.Combine("~", ".ssh", "known_hosts"));
            e.CanTrust = KnownHosts.Contains(knownHosts, options.Host, options.Port, e.HostKeyName, Convert.ToBase64String(e.HostKey));
        };

        try
        {
            client.Connect();
        }
        catch (Renci.SshNet.Common.SshConnectionException ex) when (ex.Message.Contains("Key exchange negotiation failed", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("host key", StringComparison.OrdinalIgnoreCase))
        {
            client.Dispose();
            throw new MigrationException($"ssh: the host key of {options.Host} is not trusted. Add it to known_hosts (ssh-keyscan) or set 'hostKeyFingerprint'.", ex);
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException)
        {
            client.Dispose();
            throw new MigrationException($"ssh: could not connect to {options.User}@{options.Host}:{options.Port}: {ex.Message}", ex);
        }

        var forward = new ForwardedPortLocal("127.0.0.1", 0, target.Host, (uint)target.Port);
        client.AddForwardedPort(forward);
        forward.Start();
        return new SshTunnel(client, forward);
    }

    public void Dispose()
    {
        if (forward.IsStarted)
        {
            forward.Stop();
        }

        client.Dispose();
    }

    internal static string ExpandHome(string path) =>
        path.StartsWith('~') ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\')) : path;
}

/// <summary>Minimal OpenSSH known_hosts reader, including hashed host names (|1|salt|hash).</summary>
public static class KnownHosts
{
    public static bool Contains(string file, string host, int port, string keyType, string keyBase64)
    {
        if (!File.Exists(file))
        {
            return false;
        }

        var hostPattern = port == 22 ? host : $"[{host}]:{port}";
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('@'))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !string.Equals(parts[1], keyType, StringComparison.Ordinal) || !string.Equals(parts[2], keyBase64, StringComparison.Ordinal))
            {
                continue;
            }

            if (parts[0].Split(',').Any(h => Matches(h, hostPattern)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(string entry, string host)
    {
        if (!entry.StartsWith("|1|", StringComparison.Ordinal))
        {
            return string.Equals(entry, host, StringComparison.OrdinalIgnoreCase);
        }

        var pieces = entry.Split('|');
        if (pieces.Length != 4)
        {
            return false;
        }

        var salt = Convert.FromBase64String(pieces[2]);
        var hash = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(host));
        return string.Equals(Convert.ToBase64String(hash), pieces[3], StringComparison.Ordinal);
    }
}

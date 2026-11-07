using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MystiaStewardCompanion.Core;

/// <summary>清理失败只产生启动提示；Removed 仅表示本次确实移除了旧根文件，不表示业务数据已迁移完成。</summary>
internal sealed record ModLegacyConfigCleanupResult(bool Removed, string? Warning);

internal static partial class ModDataLayoutMigration
{
    /// <summary>
    /// 真实配置绑定、Token 保存及回读成功后的单次启动收尾，仅允许删除本次布局备份中的旧根 CFG。
    /// 所有原字节备份保留；此操作不启动客户端、不访问游戏对象，也不等待或推进业务 JSON 升级。
    /// pending 绑定具体原件身份，使删除后尚未来得及记录完成便中断时，不会误删后来重建的同名文件。
    /// </summary>
    internal static ModLegacyConfigCleanupResult TryCleanupLegacyConfiguration(ModDataPaths paths,
        Action<byte[]> validateBoundConfiguration, Action<string>? checkpoint = null)
    {
        var removed = false;
        try
        {
            if (!OperatingSystem.IsWindows())
                return new(false, "当前平台未启用旧根配置自动清理，原文件保留。");
            RequireOrdinaryPath(paths.Root);
            var lockPath = Path.Combine(paths.Root, ".layout-migration.lock");
            RequireOrdinaryPath(lockPath);
            using var ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var state = ReadState(paths.LayoutStatePath);
            if (state?.Status != "complete") throw new ModDataValidationException("布局发布尚未完成，旧配置未清理。");
            var legacy = state.Files.SingleOrDefault(item => item.Key == "legacy-plugin.cfg");
            if (legacy == null)
                return new(false, Attributes(paths.LegacyPluginConfigPath).HasValue
                    ? "旧根配置不属于本次迁移备份，已保留供人工核对。" : null);

            var receipt = ReadCleanupReceipt(paths.LegacyConfigCleanupPath, out var receiptHash);
            if (receipt != null && (receipt.BackupDirectory != state.BackupDirectory || receipt.SourceSha256 != legacy.Sha256))
                throw new ModDataValidationException("旧配置清理记录与布局备份不一致，未继续清理。");
            if (receipt?.Status == "complete")
            {
                // 已完成的自动清理永不重新开启；旧版本或第三方后来写回旧路径时不能再次按名称删除。
                return new(false, Attributes(paths.LegacyPluginConfigPath).HasValue
                    ? "旧根配置在清理完成后重新出现，已保留；请检查是否仍有旧版程序写入。" : null);
            }

            var backup = Path.Combine(paths.MigrationBackupRoot, state.BackupDirectory);
            var evidence = new[] { paths.LayoutStatePath, Path.Combine(backup, "manifest.json"),
                Path.Combine(backup, PreparedConfig), paths.PluginConfigPath }
                .Concat(state.Files.Select(item => Path.Combine(backup, item.Key))).ToArray();
            using var leases = new CleanupLeases();
            // 先固定所有证明文件；父链锁在 pending 记录完成原子发布之后取得，避免阻碍同目录重命名。
            foreach (var path in evidence) leases.Open(path, delete: false);
            var acceptedState = ReadState(paths.LayoutStatePath);
            if (acceptedState == null || JsonSerializer.Serialize(acceptedState, JsonOptions) != JsonSerializer.Serialize(state, JsonOptions))
                throw new ModDataValidationException("布局记录在清理准备期间发生变化，未继续清理。");
            VerifyBackup(backup, state);
            validateBoundConfiguration(RequiredBytes(paths.PluginConfigPath, MaximumConfigBytes));

            if (!Attributes(paths.LegacyPluginConfigPath).HasValue)
            {
                // 可能由显式离线清理移除，也可能上次已完成删除。只记录终态，不重建旧源。
                receipt ??= new CleanupReceipt { BackupDirectory = state.BackupDirectory, SourceSha256 = legacy.Sha256 };
                receipt.Status = "complete";
                DurablePublish(paths.LegacyConfigCleanupPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions), receiptHash);
                return new(false, null);
            }
            var source = leases.Open(paths.LegacyPluginConfigPath, delete: true);
            var sourceBytes = source.ReadBytes(MaximumConfigBytes);
            if (sourceBytes.LongLength != legacy.Length || Hash(sourceBytes) != legacy.Sha256
                || !sourceBytes.AsSpan().SequenceEqual(RequiredBytes(Path.Combine(backup, "legacy-plugin.cfg"), MaximumConfigBytes)))
                throw new ModDataValidationException("旧根配置在迁移后发生变化，未删除尚未备份的修改。");
            if (receipt != null && receipt.SourceIdentity != source.Identity)
                throw new ModDataValidationException("待清理的旧根配置已被另一文件替换，原文件保留供人工核对。");
            if (receipt == null)
            {
                receipt = new CleanupReceipt { BackupDirectory = state.BackupDirectory, SourceSha256 = legacy.Sha256,
                    SourceIdentity = source.Identity, Status = "pending" };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions);
                DurablePublish(paths.LegacyConfigCleanupPath, bytes, null);
                receiptHash = Hash(bytes);
            }
            var receiptLease = leases.Open(paths.LegacyConfigCleanupPath, delete: false);
            if (Hash(receiptLease.ReadBytes(MaximumConfigBytes)) != receiptHash)
                throw new ModDataValidationException("旧配置清理记录在准备期间发生变化，未继续清理。");
            foreach (var path in evidence.Append(paths.LegacyPluginConfigPath).Append(paths.LegacyConfigCleanupPath))
                leases.AnchorParents(path);
            leases.ValidateAllPaths();
            checkpoint?.Invoke("after-cleanup-pending");
            checkpoint?.Invoke("before-legacy-delete");
            // 证明和源内容均仍由句柄禁止写入；删除前再次核对最终路径、身份及链接数。
            source.Delete();
            removed = true;
            checkpoint?.Invoke("after-legacy-delete");
            // 删除已经结束，释放目录守卫后才能原子更新同目录完成记录；证明文件本身继续保持锁定。
            leases.ReleaseParents();
            receiptLease.Dispose();
            receipt.Status = "complete";
            DurablePublish(paths.LegacyConfigCleanupPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions), receiptHash);
            return new(true, null);
        }
        catch (Exception error)
        {
            var reason = error is ModDataValidationException ? error.Message : $"文件访问或记录失败（{error.GetType().Name}）";
            return new(removed, removed
                ? $"旧根配置已移除，但完成记录尚未确认；原字节备份保留，下次启动将核对恢复。{reason}"
                : $"旧根配置暂未自动清理，原文件保留；不影响使用新配置。{reason}");
        }
    }

    /// <summary>清理记录独立于 layoutVersion=1 的五字段协议，不把局部清理结果写成全部数据迁移状态。</summary>
    private static CleanupReceipt? ReadCleanupReceipt(string path, out string? originalHash)
    {
        var bytes = OptionalBytes(path, MaximumConfigBytes);
        originalHash = bytes == null ? null : Hash(bytes);
        if (bytes == null) return null;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            ExactFields(document.RootElement, "version", "status", "backupDirectory", "sourceSha256", "sourceIdentity");
            var value = JsonSerializer.Deserialize<CleanupReceipt>(bytes, JsonOptions)!;
            var identityValid = value.SourceIdentity != null && value.SourceIdentity.Length == 42
                && value.SourceIdentity[8] == ':' && value.SourceIdentity[25] == ':'
                && value.SourceIdentity.Where((_, index) => index is not (8 or 25)).All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
            if (value.Version != 1 || value.Status is not ("pending" or "complete")
                || !ValidHash(value.SourceSha256) || string.IsNullOrEmpty(value.BackupDirectory)
                || (value.Status == "pending" ? !identityValid : !(identityValid || value.SourceIdentity == "")))
                throw new ModDataValidationException("旧配置清理记录版本或内容无效，未自动重建。");
            return value;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or NullReferenceException)
        { throw new ModDataValidationException("旧配置清理记录损坏，未自动重建。"); }
    }

    private sealed class CleanupReceipt
    {
        public int Version { get; set; } = 1;
        public string Status { get; set; } = "pending";
        public string BackupDirectory { get; set; } = "";
        public string SourceSha256 { get; set; } = "";
        public string SourceIdentity { get; set; } = "";
    }

    /// <summary>整个清理阶段的文件及父目录守卫；关闭只释放句柄，只有显式 Delete 会标记旧 CFG 删除。</summary>
    private sealed class CleanupLeases : IDisposable
    {
        private readonly Dictionary<string, CleanupFile> _parents = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<CleanupFile> _files = new();
        internal void AnchorParents(string path)
        {
            for (var current = Path.GetDirectoryName(Path.GetFullPath(path)); current != null && Path.GetDirectoryName(current) != null;
                current = Path.GetDirectoryName(current))
                if (!_parents.ContainsKey(current)) _parents.Add(current, new CleanupFile(current, delete: false, directory: true));
        }
        internal CleanupFile Open(string path, bool delete)
        {
            var value = new CleanupFile(path, delete, directory: false);
            _files.Add(value);
            return value;
        }
        internal void ValidateAllPaths()
        {
            foreach (var value in _files) value.Validate();
        }
        internal void ReleaseParents()
        {
            foreach (var value in _parents.Values) value.Dispose();
            _parents.Clear();
        }
        public void Dispose()
        {
            foreach (var value in _files) value.Dispose();
            ReleaseParents();
        }
    }

    /// <summary>
    /// Windows 句柄只共享读取，阻止文件写入及替换；源额外申请 DELETE 权限，但不以路径执行删除。
    /// 目录父链另持非 DELETE 共享句柄；最终路径与原批准路径必须一致，硬链接和重解析点均拒绝。
    /// </summary>
    private sealed class CleanupFile : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly string _path;
        private readonly bool _directory;
        private readonly bool _delete;
        private readonly NativeFileInfo _original;
        internal string Identity => IdentityOf(_original);
        internal CleanupFile(string path, bool delete, bool directory)
        {
            _path = Path.GetFullPath(path);
            _directory = directory;
            _delete = delete;
            RequireOrdinaryPath(_path);
            _handle = CreateFileW(_path, 0x80000000u | (delete ? 0x10000u : 0), 1, IntPtr.Zero, 3,
                0x00200000u | 0x02000000u, IntPtr.Zero);
            if (_handle.IsInvalid) { _handle.Dispose(); throw new IOException("无法锁定旧配置清理对象。"); }
            try { _original = Inspect(); }
            catch { _handle.Dispose(); throw; }
        }
        internal byte[] ReadBytes(int maximum)
        {
            var length = ((ulong)_original.FileSizeHigh << 32) | _original.FileSizeLow;
            if (_directory || length > (ulong)maximum) throw new ModDataValidationException("旧根配置大小或类型无效。");
            // FileStream 仅借用同一稳定句柄；外层 CleanupFile 保持其所有权直至最终删除/释放。
            using var borrowed = new SafeFileHandle(_handle.DangerousGetHandle(), ownsHandle: false);
            using var stream = new FileStream(borrowed, FileAccess.Read);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            if ((ulong)buffer.Length != length) throw new ModDataValidationException("旧根配置在读取期间发生变化。");
            return buffer.ToArray();
        }
        internal void Delete()
        {
            var current = Inspect();
            if (!_delete || _directory || IdentityOf(current) != Identity
                || current.Attributes != _original.Attributes || current.LastWriteTime != _original.LastWriteTime
                || current.FileSizeHigh != _original.FileSizeHigh || current.FileSizeLow != _original.FileSizeLow)
                throw new ModDataValidationException("旧根配置身份在清理期间发生变化，未执行删除。");
            var disposition = new FileDisposition { DeleteFile = true };
            if (!SetFileInformationByHandle(_handle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
                throw new IOException("旧根配置删除未完成。");
            _handle.Dispose();
        }
        internal void Validate()
        {
            var info = Inspect();
            if (IdentityOf(info) != Identity || info.LastWriteTime != _original.LastWriteTime
                || info.FileSizeHigh != _original.FileSizeHigh || info.FileSizeLow != _original.FileSizeLow)
                throw new ModDataValidationException("清理证明对象在准入期间发生变化。");
        }
        private NativeFileInfo Inspect()
        {
            if (!GetFileInformationByHandle(_handle, out var info)) throw new IOException("无法读取旧配置清理对象身份。");
            if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != _directory
                || (!_directory && info.Links != 1)) throw new ModDataValidationException("旧配置清理对象包含链接或类型不符。");
            var name = new StringBuilder(32768);
            var size = GetFinalPathNameByHandleW(_handle, name, (uint)name.Capacity, 0);
            if (size == 0 || size >= name.Capacity || !string.Equals(Normalize(name.ToString()), Normalize(_path), StringComparison.OrdinalIgnoreCase))
                throw new ModDataValidationException("旧配置清理对象已离开批准位置。");
            return info;
        }
        private static string Normalize(string value) => (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + value[8..] : value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value[4..] : value).TrimEnd('\\', '/');
        private static string IdentityOf(NativeFileInfo value) => $"{value.VolumeSerial:x8}:{value.FileIndexHigh:x8}{value.FileIndexLow:x8}:{value.CreationTime:x16}";
        public void Dispose() => _handle.Dispose();
    }

    // Win32 FILETIME 由两个 DWORD 构成、按 4 字节对齐；即使这里用 ulong 表示，也不能采用 8 字节自然对齐。
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct NativeFileInfo
    {
        public uint Attributes;
        public ulong CreationTime;
        public ulong LastAccessTime;
        public ulong LastWriteTime;
        public uint VolumeSerial;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Links;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeFileInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder name, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref FileDisposition info, uint size);
}

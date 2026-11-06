namespace MystiaStewardCompanion.Core;

/// <summary>稀客目录中的数值身份与展示名称；不代表某次到店或某笔订单的运行时身份。</summary>
public sealed record RareCustomerIdentity(int Id, string Name);

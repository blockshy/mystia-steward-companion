import { useMemo, type ReactNode } from 'react';
import { BusinessContext, type BusinessConnection } from '@/companion/business-connection';

/** 页面只接收连接与版本信息；库存、目录及生效策略由 C# 宿主提供，不能作为查询参数覆盖。 */
export function BusinessConnectionProvider({ children, ...connection }: BusinessConnection & { children: ReactNode }) {
  const { endpoint, apiToken, snapshotSignature, enabled } = connection;
  const value = useMemo(() => ({ endpoint, apiToken, snapshotSignature, enabled }),
    [endpoint, apiToken, snapshotSignature, enabled]);
  return <BusinessContext.Provider value={value}>{children}</BusinessContext.Provider>;
}

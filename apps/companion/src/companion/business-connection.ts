import { createContext, useContext } from 'react';

/** 业务页面的最小连接信息；不能夹带可覆盖服务端库存、目录或策略的数据。 */
export interface BusinessConnection {
  endpoint: string;
  apiToken: string;
  snapshotSignature: string;
  enabled: boolean;
}

export const BusinessContext = createContext<BusinessConnection | null>(null);

/** 读取上层工作台提供的连接。脱离 Provider 时显式失败，避免页面静默连接默认端口。 */
export function useBusinessConnection(): BusinessConnection {
  const connection = useContext(BusinessContext);
  if (!connection) throw new Error('推荐页面未连接 C# 业务宿主。');
  return connection;
}

import { createAuditApi } from '@mechware/api-client';
import { api } from './api';

export const auditApi = createAuditApi(api);

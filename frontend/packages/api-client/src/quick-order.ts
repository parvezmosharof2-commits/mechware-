import type { MechWareApiClient } from './index';
import type { QuickOrderEnvelope, QuickOrderResult } from './quick-order-model';

/**
 * Typed wrapper for POST /api/v1/quick-order/validate. Only the pasted text is sent; prices and ids never are.
 * The response is unwrapped to the result. 413 / 422 / 429 reject with the problem object (see classifyQuickOrderError).
 */
export function createQuickOrderApi(client: MechWareApiClient) {
  return {
    validate: (text: string, signal?: AbortSignal): Promise<QuickOrderResult> =>
      client
        .fetch<QuickOrderEnvelope>('/api/v1/quick-order/validate', { method: 'POST', body: JSON.stringify({ text }), signal })
        .then((r) => r.data)
  };
}

export type QuickOrderApi = ReturnType<typeof createQuickOrderApi>;

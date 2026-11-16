import type { MechWareApiClient } from './index';
import type { EffectiveFloor, EffectiveQuery, MarginFloorRow, UpsertFloorBody } from './margin-floors-model';
import { effectiveQuery } from './margin-floors-model';

type Data<T> = { data: T };

/**
 * Staff margin floors (`/api/v1/admin/margin-floors`, D2.9). Read and effective need pricing.view_cost; upsert and remove need
 * pricing.manage_floors and a fresh authenticator code in `X-Step-Up-Code` (purposes margin-floor.change / margin-floor.delete).
 * A code is spent even when the call is then refused. Response types are hand-written from the D2.9 HANDOFF (the OpenAPI document has no response bodies for these).
 */
export function createMarginFloorsApi(client: MechWareApiClient) {
  const base = '/api/v1/admin/margin-floors';
  const stepUp = (code: string) => ({ 'X-Step-Up-Code': code.trim() });
  return {
    list: () => client.fetch<Data<MarginFloorRow[]>>(base),
    effective: (q: EffectiveQuery) => client.fetch<Data<EffectiveFloor>>(`${base}/effective?${effectiveQuery(q)}`),
    upsert: (body: UpsertFloorBody, stepUpCode: string) =>
      client.fetch<Data<MarginFloorRow>>(base, { method: 'PUT', body: JSON.stringify(body), headers: stepUp(stepUpCode) }),
    remove: (id: string, stepUpCode: string) =>
      client.fetch<unknown>(`${base}/${encodeURIComponent(id)}`, { method: 'DELETE', headers: stepUp(stepUpCode) })
  };
}

export type MarginFloorsApi = ReturnType<typeof createMarginFloorsApi>;

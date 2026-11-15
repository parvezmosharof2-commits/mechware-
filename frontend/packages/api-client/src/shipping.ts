import type { MechWareApiClient } from './index';
import type {
  ShippingCoverage, ShippingPreviewRequest, ShippingPreviewResult, ShippingZoneBody, ShippingZoneDetail, ShippingZoneReplaceBody, ShippingZoneSummary
} from './shipping-model';

type Data<T> = { data: T };

/** Staff shipping zones (`/api/v1/admin/shipping`, E8.4a). Read needs shipping.view, writes shipping.manage. */
export function createShippingZonesApi(client: MechWareApiClient) {
  const base = '/api/v1/admin/shipping';
  const id = (v: string) => encodeURIComponent(v);
  const json = (method: string, body?: unknown): RequestInit => ({ method, body: body === undefined ? undefined : JSON.stringify(body) });
  return {
    list: () => client.fetch<Data<ShippingZoneSummary[]>>(`${base}/zones`),
    get: (zoneId: string) => client.fetch<Data<ShippingZoneDetail>>(`${base}/zones/${id(zoneId)}`),
    create: (body: ShippingZoneBody) => client.fetch<Data<ShippingZoneDetail>>(`${base}/zones`, json('POST', body)),
    /** Whole-document replace; `expectedVersion` is the version the editor loaded (409 SHIPPING_ZONE_STALE when it moved on). */
    replace: (zoneId: string, body: ShippingZoneReplaceBody) => client.fetch<Data<ShippingZoneDetail>>(`${base}/zones/${id(zoneId)}`, json('PUT', body)),
    delete: (zoneId: string) => client.fetch<unknown>(`${base}/zones/${id(zoneId)}`, json('DELETE')),
    preview: (body: ShippingPreviewRequest) => client.fetch<Data<ShippingPreviewResult>>(`${base}/zones/preview`, json('POST', body)),
    coverage: () => client.fetch<Data<ShippingCoverage>>(`${base}/coverage`)
  };
}

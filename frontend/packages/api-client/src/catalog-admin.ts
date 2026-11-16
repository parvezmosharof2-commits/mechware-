import type { MechWareApiClient } from './index';
import type { ProductPim, SetSupplyBody } from './catalog-admin-model';

type Data<T> = { data: T };

/** Staff product information (`/api/v1/admin/products/{id}/...`, needs catalog.manage). Only the verbs the API has. */
export function createCatalogAdminApi(client: MechWareApiClient) {
  const id = (v: string) => encodeURIComponent(v);
  const put = (body: unknown): RequestInit => ({ method: 'PUT', body: JSON.stringify(body) });
  const base = (productId: string) => `/api/v1/admin/products/${id(productId)}`;
  return {
    pim: (productId: string) => client.fetch<Data<ProductPim>>(`${base(productId)}/pim`),
    setSupply: (productId: string, body: SetSupplyBody) => client.fetch<Data<ProductPim>>(`${base(productId)}/supply`, put(body)),
    setAlternatives: (productId: string, alternativeProductIds: string[]) => client.fetch<Data<ProductPim>>(`${base(productId)}/alternatives`, put({ alternativeProductIds })),
    setOrderUnits: (productId: string, variantId: string, body: { minOrderQty: number; packSize: number }) =>
      client.fetch<Data<ProductPim>>(`${base(productId)}/variants/${id(variantId)}/order-units`, put(body)),
    setPartNumber: (productId: string, variantId: string, manufacturerPartNumber: string | null) =>
      client.fetch<Data<ProductPim>>(`${base(productId)}/variants/${id(variantId)}/part-number`, put({ manufacturerPartNumber }))
  };
}

import type { MechWareApiClient } from './index';
import { lookupQuery, productListQuery } from './catalog-admin-model';
import type { BrandOption, CategoryOption, ProductBasicsBody, AdminProductDetail, ProductListFilters, ProductListItem, ProductPim, SetSupplyBody, VariantEditBody } from './catalog-admin-model';

type Data<T> = { data: T };
type Page<T> = { items: T[]; page: number; pageSize: number; totalCount: number };

/** Staff product information (`/api/v1/admin/products/{id}/...`, needs catalog.manage). Only the verbs the API has. */
export function createCatalogAdminApi(client: MechWareApiClient) {
  const id = (v: string) => encodeURIComponent(v);
  const put = (body: unknown): RequestInit => ({ method: 'PUT', body: JSON.stringify(body) });
  const base = (productId: string) => `/api/v1/admin/products/${id(productId)}`;
  const post = (body: unknown): RequestInit => ({ method: 'POST', body: JSON.stringify(body) });
  const lifecycle = (action: string) => (productId: string, expectedVersion: number) => client.fetch<Data<AdminProductDetail>>(`${base(productId)}/${action}`, post({ expectedVersion }));
  return {
    productsList: (f: ProductListFilters = {}) => client.fetch<Data<Page<ProductListItem>>>(`/api/v1/admin/products?${productListQuery(f)}`),
    productGet: (productId: string) => client.fetch<Data<AdminProductDetail>>(base(productId)),
    productUpdate: (productId: string, body: ProductBasicsBody) => client.fetch<Data<AdminProductDetail>>(base(productId), put(body)),
    variantUpdate: (productId: string, variantId: string, body: VariantEditBody) => client.fetch<Data<AdminProductDetail>>(`${base(productId)}/variants/${id(variantId)}`, put(body)),
    publish: lifecycle('publish'),
    archive: lifecycle('archive'),
    restore: lifecycle('restore'),
    brands: (q: string) => client.fetch<Data<Page<BrandOption>>>(`/api/v1/admin/brands?${lookupQuery(q)}`),
    categories: (q: string) => client.fetch<Data<Page<CategoryOption>>>(`/api/v1/admin/categories?${lookupQuery(q)}`),
    pim: (productId: string) => client.fetch<Data<ProductPim>>(`${base(productId)}/pim`),
    setSupply: (productId: string, body: SetSupplyBody) => client.fetch<Data<ProductPim>>(`${base(productId)}/supply`, put(body)),
    setAlternatives: (productId: string, alternativeProductIds: string[]) => client.fetch<Data<ProductPim>>(`${base(productId)}/alternatives`, put({ alternativeProductIds })),
    setOrderUnits: (productId: string, variantId: string, body: { minOrderQty: number; packSize: number }) =>
      client.fetch<Data<ProductPim>>(`${base(productId)}/variants/${id(variantId)}/order-units`, put(body)),
    setPartNumber: (productId: string, variantId: string, manufacturerPartNumber: string | null) =>
      client.fetch<Data<ProductPim>>(`${base(productId)}/variants/${id(variantId)}/part-number`, put({ manufacturerPartNumber }))
  };
}

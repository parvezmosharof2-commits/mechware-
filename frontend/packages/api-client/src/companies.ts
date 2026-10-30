import type { MechWareApiClient } from './index';
import type {
  AddMemberRequest,
  CompanyDto,
  CostCentreDto,
  CostCentreRequest,
  MemberDto,
  MyCompanyDto,
  PagedResult,
  RegisterCompanyRequest,
  SetCreditRequest,
  SiteDto,
  SiteRequest,
  UpdateMemberRequest
} from './companies-model';

type Data<T> = { data: T };

/**
 * Typed wrappers for /api/v1/companies (members) and /api/v1/admin/companies (MECHWARE staff).
 * Pass the shared MechWareApiClient so token handling and 401 refresh stay in one place.
 */
export function createCompaniesApi(client: MechWareApiClient) {
  const json = (method: string, body?: unknown): RequestInit => ({
    method,
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const base = (companyId: string) => `/api/v1/companies/${encodeURIComponent(companyId)}`;

  return {
    register: (body: RegisterCompanyRequest) => client.fetch<Data<CompanyDto>>('/api/v1/companies', json('POST', body)),
    listMine: () => client.fetch<Data<MyCompanyDto[]>>('/api/v1/companies'),
    get: (companyId: string) => client.fetch<Data<CompanyDto>>(base(companyId)),

    listSites: (companyId: string) => client.fetch<Data<SiteDto[]>>(`${base(companyId)}/sites`),
    addSite: (companyId: string, body: SiteRequest) => client.fetch<Data<SiteDto>>(`${base(companyId)}/sites`, json('POST', body)),
    updateSite: (companyId: string, siteId: string, body: SiteRequest) =>
      client.fetch<Data<SiteDto>>(`${base(companyId)}/sites/${encodeURIComponent(siteId)}`, json('PUT', body)),
    removeSite: (companyId: string, siteId: string) =>
      client.fetch<unknown>(`${base(companyId)}/sites/${encodeURIComponent(siteId)}`, json('DELETE')),

    listCostCentres: (companyId: string) => client.fetch<Data<CostCentreDto[]>>(`${base(companyId)}/cost-centres`),
    addCostCentre: (companyId: string, body: CostCentreRequest) =>
      client.fetch<Data<CostCentreDto>>(`${base(companyId)}/cost-centres`, json('POST', body)),
    updateCostCentre: (companyId: string, id: string, body: CostCentreRequest) =>
      client.fetch<Data<CostCentreDto>>(`${base(companyId)}/cost-centres/${encodeURIComponent(id)}`, json('PUT', body)),
    removeCostCentre: (companyId: string, id: string) =>
      client.fetch<unknown>(`${base(companyId)}/cost-centres/${encodeURIComponent(id)}`, json('DELETE')),

    listMembers: (companyId: string) => client.fetch<Data<MemberDto[]>>(`${base(companyId)}/members`),
    addMember: (companyId: string, body: AddMemberRequest) =>
      client.fetch<Data<MemberDto>>(`${base(companyId)}/members`, json('POST', body)),
    updateMember: (companyId: string, memberId: string, body: UpdateMemberRequest) =>
      client.fetch<Data<MemberDto>>(`${base(companyId)}/members/${encodeURIComponent(memberId)}`, json('PUT', body)),
    removeMember: (companyId: string, memberId: string) =>
      client.fetch<unknown>(`${base(companyId)}/members/${encodeURIComponent(memberId)}`, json('DELETE')),

    admin: {
      list: (params: { status?: string; page?: number; pageSize?: number } = {}) => {
        const q = new URLSearchParams();
        if (params.status) q.set('status', params.status);
        if (params.page) q.set('page', String(params.page));
        if (params.pageSize) q.set('pageSize', String(params.pageSize));
        const qs = q.toString();
        return client.fetch<Data<PagedResult<CompanyDto>>>(`/api/v1/admin/companies${qs ? `?${qs}` : ''}`);
      },
      get: (companyId: string) => client.fetch<Data<CompanyDto>>(`/api/v1/admin/companies/${encodeURIComponent(companyId)}`),
      activate: (companyId: string) =>
        client.fetch<Data<CompanyDto>>(`/api/v1/admin/companies/${encodeURIComponent(companyId)}/activate`, json('POST')),
      suspend: (companyId: string) =>
        client.fetch<Data<CompanyDto>>(`/api/v1/admin/companies/${encodeURIComponent(companyId)}/suspend`, json('POST')),
      setCredit: (companyId: string, body: SetCreditRequest) =>
        client.fetch<Data<CompanyDto>>(`/api/v1/admin/companies/${encodeURIComponent(companyId)}/credit`, json('PUT', body))
    }
  };
}

export type CompaniesApi = ReturnType<typeof createCompaniesApi>;

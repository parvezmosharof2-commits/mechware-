/**
 * B2B company account types and pure helpers. No runtime imports, so it can be unit-tested directly with
 * Node's type stripping. Field names mirror the API DTOs in ShopVerse.Application.Companies.
 */

export interface MoneyDto {
  minorUnits: number;
  currency: string;
}

export type CompanyStatus = 'pending' | 'active' | 'suspended';
export type CreditTermsName = 'none' | 'net15' | 'net30';
/** Numeric value sent to the API (days). */
export type CreditTermsValue = 0 | 15 | 30;

export interface CompanyDto {
  id: string;
  legalName: string;
  tradeLicenceNo: string;
  bin: string;
  tin: string | null;
  industry: string | null;
  status: CompanyStatus;
  creditTerms: CreditTermsName;
  creditLimit: MoneyDto;
  creditUsed: MoneyDto;
  creditAvailable: MoneyDto;
  createdAtUtc: string;
}

export interface MyCompanyDto {
  company: CompanyDto;
  roles: number;
}

export interface RegisterCompanyRequest {
  legalName: string;
  tradeLicenceNo: string;
  bin: string;
  tin?: string | null;
  industry?: string | null;
}

export interface SiteRequest {
  name: string;
  contactName: string;
  contactPhone: string;
  line1: string;
  line2?: string | null;
  upazila: string;
  district: string;
  division: string;
  postalCode?: string | null;
  isDefaultDelivery: boolean;
}

export interface SiteDto extends Omit<SiteRequest, 'line2' | 'postalCode'> {
  id: string;
  line2: string | null;
  postalCode: string | null;
}

export interface CostCentreRequest {
  code: string;
  name: string;
  monthlyBudgetMinor?: number | null;
}

export interface CostCentreDto {
  id: string;
  code: string;
  name: string;
  monthlyBudgetMinor: number | null;
}

export interface AddMemberRequest {
  userId: string;
  roles: number;
  spendLimitMinor?: number | null;
  costCentreIds?: string[];
}

export interface UpdateMemberRequest {
  roles: number;
  spendLimitMinor?: number | null;
  costCentreIds?: string[];
  isActive: boolean;
}

export interface MemberDto {
  id: string;
  userId: string;
  roles: number;
  spendLimitMinor: number | null;
  isActive: boolean;
  costCentreIds: string[];
}

export interface SetCreditRequest {
  terms: CreditTermsValue;
  limitMinorUnits: number;
}

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

/** Company-scoped roles; a member may hold several (bit flags, same values as the API's CompanyRoles). */
export const CompanyRoles = { Requester: 1, Buyer: 2, Approver: 4, Admin: 8 } as const;
export type CompanyRoleName = keyof typeof CompanyRoles;

const ALL_ROLE_BITS = 1 | 2 | 4 | 8;

export function hasRole(roles: number, role: number): boolean {
  return (roles & role) !== 0;
}

export function isValidRoles(roles: number): boolean {
  return Number.isInteger(roles) && roles !== 0 && (roles & ~ALL_ROLE_BITS) === 0;
}

export function roleNames(roles: number): CompanyRoleName[] {
  return (Object.keys(CompanyRoles) as CompanyRoleName[]).filter((name) => hasRole(roles, CompanyRoles[name]));
}

export function rolesFromNames(names: readonly CompanyRoleName[]): number {
  return names.reduce((acc, name) => acc | CompanyRoles[name], 0);
}

/** Taka (or other major unit) from minor units. All supported currencies use 2 decimals. */
export function minorToAmount(money: MoneyDto): number {
  return money.minorUnits / 100;
}

/** Whole minor units from a user-entered major amount; rounds half away from zero to avoid float drift. */
export function amountToMinor(amount: number): number {
  return Math.round((amount + Number.EPSILON) * 100);
}

/** Share of the credit limit already used, 0-100. Zero when no limit is set. */
export function creditUsagePercent(company: Pick<CompanyDto, 'creditLimit' | 'creditUsed'>): number {
  const limit = company.creditLimit.minorUnits;
  if (limit <= 0) return 0;
  return Math.min(100, Math.max(0, Math.round((company.creditUsed.minorUnits / limit) * 100)));
}

export function creditTermsValue(name: CreditTermsName): CreditTermsValue {
  return name === 'net15' ? 15 : name === 'net30' ? 30 : 0;
}

const BANGLA_DIGITS = '০১২৩৪৫৬৭৮৯';

/** Converts Bangla digits to ASCII and strips spaces / dashes, so BIN and TIN typed in Bangla still validate. */
export function normalizeDigits(value: string): string {
  return value
    .replace(/[০-৯]/g, (d) => String(BANGLA_DIGITS.indexOf(d)))
    .replace(/[\s-]/g, '');
}

export type CompanyFieldError =
  | 'legalName.length'
  | 'tradeLicenceNo.length'
  | 'bin.format'
  | 'tin.format'
  | 'industry.length';

/**
 * Same rules as the API's Company.Register. Returns error codes (not text) so each UI localises them.
 * The API remains the authority; this only saves a round trip.
 */
export function validateCompanyRegistration(input: RegisterCompanyRequest): Partial<Record<keyof RegisterCompanyRequest, CompanyFieldError>> {
  const errors: Partial<Record<keyof RegisterCompanyRequest, CompanyFieldError>> = {};
  const legalName = input.legalName.trim();
  const licence = input.tradeLicenceNo.trim();
  const bin = normalizeDigits(input.bin);
  const tin = normalizeDigits(input.tin ?? '');
  if (legalName.length < 2 || legalName.length > 200) errors.legalName = 'legalName.length';
  if (licence.length < 3 || licence.length > 50) errors.tradeLicenceNo = 'tradeLicenceNo.length';
  if (!/^\d{13}$/.test(bin)) errors.bin = 'bin.format';
  if (tin.length > 0 && !/^\d{12}$/.test(tin)) errors.tin = 'tin.format';
  if ((input.industry ?? '').trim().length > 100) errors.industry = 'industry.length';
  return errors;
}

/** Request body ready to send: trimmed, digits normalised, empty optionals as null. */
export function toRegisterRequest(input: RegisterCompanyRequest): RegisterCompanyRequest {
  const tin = normalizeDigits(input.tin ?? '');
  const industry = (input.industry ?? '').trim();
  return {
    legalName: input.legalName.trim(),
    tradeLicenceNo: input.tradeLicenceNo.trim(),
    bin: normalizeDigits(input.bin),
    tin: tin.length > 0 ? tin : null,
    industry: industry.length > 0 ? industry : null
  };
}

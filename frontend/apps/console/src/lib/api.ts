import {
  MechWareApiClient, fulfilErrorText, shouldReloadFulfil, type FulfilStep, catalogAdminErrorText, createCreditPaymentsApi, createStaffOrdersApi, createStaffQuotesApi, deskErrorText, shouldReloadDesk, creditErrorText, shouldReloadStaffOrders, staffOrderErrorText, shouldReloadCredit, createAdminLookupsApi, createCatalogAdminApi, createCatalogImportApi, createCompaniesApi, createMarginFloorsApi, createPriceListsApi, createShippingZonesApi, createStaffApi, createStorefrontApi, createQuoteTermsApi, termsErrorText,
  decisionErrorText, floorErrorText, mapServerFields, priceListErrorText, shouldReloadAfter, problemCode, problemFieldErrors, problemMessage, problemStatus, shippingErrorText
} from '@mechware/api-client';

import { createStaffInvoicesApi, createStaffReturnsApi, shouldReloadReturn, staffReturnErrorText } from '@mechware/api-client';

export const API_BASE_URL = process.env.NEXT_PUBLIC_API_BASE_URL ?? 'http://localhost:5000';

export const api = new MechWareApiClient({ baseUrl: API_BASE_URL });
export const companiesApi = createCompaniesApi(api);
export const staffApi = createStaffApi(api);
export const catalogImportApi = createCatalogImportApi(api);
export const catalogAdminApi = createCatalogAdminApi(api);
export const lookupsApi = createAdminLookupsApi(api);
export const priceListsApi = createPriceListsApi(api);
export const marginFloorsApi = createMarginFloorsApi(api);
export const shippingZonesApi = createShippingZonesApi(api);
export const storefrontApi = createStorefrontApi(api);
export const creditPaymentsApi = createCreditPaymentsApi(api);
export const staffOrdersApi = createStaffOrdersApi(api);
export const staffQuotesApi = createStaffQuotesApi(api);
export const quoteTermsApi = createQuoteTermsApi(api);
export const staffReturnsApi = createStaffReturnsApi(api);
export const staffInvoicesApi = createStaffInvoicesApi(api);

export { problemCode, problemFieldErrors, problemMessage, problemStatus };

/** Plain-words text for a failed price-list call, and whether a retry makes sense (server error or no connection). */
export function priceListError(err: unknown): { text: string; retry: boolean; status: number | null; fields: Record<string, string> } {
  const status = problemStatus(err);
  const fields = problemFieldErrors(err);
  const text = priceListErrorText({ status, code: problemCode(err), message: problemMessage(err, ''), fields });
  return { text, retry: status === null || status >= 500, status, fields };
}

/** Plain words for a failed approve, reject or withdraw; `code` drives what the screen does next (keep the code dialog open, or reload). */
export function decisionError(err: unknown): { text: string; code: string | null; status: number | null; reload: boolean; retry: boolean } {
  const status = problemStatus(err);
  const code = problemCode(err);
  const text = decisionErrorText({ status, code, message: problemMessage(err, ''), fields: problemFieldErrors(err) });
  return { text, code, status, reload: shouldReloadAfter(code), retry: status === null || status >= 500 };
}

/** Plain words for a failed margin-floors call; `code` tells the screen whether the code dialog stays open (STEP_UP_*). */
export function floorError(err: unknown): { text: string; code: string | null; status: number | null; retry: boolean } {
  const status = problemStatus(err);
  const code = problemCode(err);
  return { text: floorErrorText({ status, code, message: problemMessage(err, ''), fields: problemFieldErrors(err) }), code, status, retry: status === null || status >= 500 };
}

/** Plain-words text for a failed shipping-zone call, the server's field errors (by form key) and whether a retry makes sense. */
export function shippingError(err: unknown): { text: string; retry: boolean; status: number | null; code: string | null; fields: Record<string, string> } {
  const status = problemStatus(err);
  const fields = mapServerFields(problemFieldErrors(err));
  const code = problemCode(err);
  return { text: shippingErrorText({ status, code, message: problemMessage(err, ''), fields }), retry: status === null || status >= 500, status, code, fields };
}

/** Roles allowed into the operations console. Buyers (storefront customers) are not. */
export const STAFF_ROLES = ['admin', 'finance', 'support', 'moderator', 'seller'] as const;

export function isStaff(roles: readonly string[]): boolean {
  return roles.some((r) => (STAFF_ROLES as readonly string[]).includes(r.toLowerCase()));
}

/** Plain-words text for a failed product-information call, the server's field errors (by field name) and whether a retry makes sense. */
export function catalogError(err: unknown): { text: string; retry: boolean; status: number | null; code: string | null; conflict: boolean; fields: Record<string, string> } {
  const status = problemStatus(err);
  const code = problemCode(err);
  const fields = problemFieldErrors(err);
  const message = problemMessage(err, '');
  return { text: catalogAdminErrorText({ status, code, message, fields }), retry: status === null || status >= 500, status, code, conflict: status === 409, fields };
}

/** Plain words for a failed credit-payment call, the server's field messages, and what the screen does next (keep the code dialog, reload, retry). */
export function creditError(err: unknown): { text: string; code: string | null; status: number | null; fields: Record<string, string>; reload: boolean; retry: boolean } {
  const status = problemStatus(err);
  const code = problemCode(err);
  const fields = problemFieldErrors(err);
  return { text: creditErrorText({ status, code, message: problemMessage(err, ''), fields }), code, status, fields, reload: shouldReloadCredit(code), retry: status === null || status >= 500 };
}

/** Plain words for a failed staff order call (cancel, refunds); `code` tells the screen whether the code dialog stays open (STEP_UP_*) and `reload` whether what it showed is stale. */
export function staffOrderError(err: unknown): { text: string; code: string | null; status: number | null; fields: Record<string, string>; reload: boolean; retry: boolean } {
  const status = problemStatus(err);
  const code = problemCode(err);
  const fields = problemFieldErrors(err);
  return { text: staffOrderErrorText({ status, code, message: problemMessage(err, ''), fields }), code, status, fields, reload: shouldReloadStaffOrders(code), retry: status === null || status >= 500 };
}

/** Plain words for a failed fulfilment move; `reload` says the order changed under the screen (ORDER_CHANGED, ORDER_INVALID_TRANSITION); `fields` are the server's ship field errors. */
export function fulfilError(step: FulfilStep, err: unknown): { text: string; code: string | null; status: number | null; fields: Record<string, string>; reload: boolean } {
  const status = problemStatus(err);
  const code = problemCode(err);
  const fields = problemFieldErrors(err);
  return { text: fulfilErrorText(step, { status, code, message: problemMessage(err, ''), fields }), code, status, fields, reload: shouldReloadFulfil(code) };
}

/** Plain words for a failed quote-desk call; `reload` says the screen shows an old state (a 409 race) and must be reloaded. */
export function deskError(err: unknown): { text: string; code: string | null; status: number | null; fields: Record<string, string>; reload: boolean; retry: boolean } {
  const status = problemStatus(err);
  const code = problemCode(err);
  const fields = problemFieldErrors(err);
  return { text: deskErrorText({ status, code, message: problemMessage(err, ''), fields }), code, status, fields, reload: shouldReloadDesk(code), retry: status === null || status >= 500 };
}

/** Plain words for a failed default-terms call; `code` tells the screen whether it is the "someone changed these" case. */
export function termsError(err: unknown): { text: string; code: string | null; status: number | null; retry: boolean } {
  const status = problemStatus(err);
  const code = problemCode(err);
  return { text: termsErrorText({ status, code, message: problemMessage(err, ''), fields: problemFieldErrors(err) }), code, status, retry: status === null || status >= 500 };
}

/** Plain words for a failed return-desk call; `reload` says the return changed under the screen (RETURN_CHANGED, RETURN_INVALID_TRANSITION). */
export function staffReturnError(err: unknown): { text: string; code: string | null; status: number | null; reload: boolean; retry: boolean } {
  const status = problemStatus(err);
  const code = problemCode(err);
  return { text: staffReturnErrorText({ status, code, message: problemMessage(err, ''), fields: problemFieldErrors(err) }), code, status, reload: shouldReloadReturn(code), retry: status === null || status >= 500 };
}

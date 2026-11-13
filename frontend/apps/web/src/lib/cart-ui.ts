import type { CartErrorKind } from '@mechware/api-client';

/** Message key (under `Cart`) for a failed change. */
export function actionErrorKey(kind: CartErrorKind): string {
  switch (kind) {
    case 'unavailable':
      return 'actionUnavailable';
    case 'busy':
      return 'actionBusy';
    case 'insufficientStock':
      return 'actionInsufficientStock';
    case 'notFound':
      return 'actionNotFound';
    case 'cartFull':
      return 'actionCartFull';
    case 'rateLimited':
      return 'actionRateLimited';
    case 'invalid':
      return 'actionInvalid';
    default:
      return 'actionOther';
  }
}

/** Message key (under `Product`) for a failed add-to-cart. */
export function addErrorKey(kind: CartErrorKind): string {
  switch (kind) {
    case 'unavailable':
      return 'addErrorUnavailable';
    case 'notFound':
      return 'addErrorNotFound';
    case 'insufficientStock':
      return 'addErrorStock';
    case 'cartFull':
      return 'addErrorFull';
    case 'invalid':
      return 'addErrorInvalid';
    default:
      return 'addErrorOther';
  }
}

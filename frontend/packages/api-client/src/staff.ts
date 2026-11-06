import type { MechWareApiClient } from './index';

type Data<T> = { data: T };

/** A staff member whose role demands an authenticator and who has none yet. */
export interface StaffAwaitingTwoFactor {
  userId: string;
  email: string;
  fullName: string;
  roles: string[];
  /** An unexpired, unused setup code is already out; issuing a new one cancels it. */
  hasActiveSetupCode: boolean;
}

export interface TwoFactorSetupCode {
  userId: string;
  /** Shown once. Hand it over in person or through another trusted channel. */
  setupCode: string;
  expiresAtUtc: string;
}

/** Staff onboarding (threat model M1): admins issue the one-time code a new staff member enrols their authenticator with. */
export function createStaffApi(client: MechWareApiClient) {
  return {
    listAwaitingTwoFactor: () => client.fetch<Data<StaffAwaitingTwoFactor[]>>('/api/v1/admin/users/awaiting-two-factor'),
    /** Needs a fresh authenticator code of the admin (step-up). */
    issueSetupCode: (userId: string, stepUpCode: string) =>
      client.fetch<Data<TwoFactorSetupCode>>(`/api/v1/admin/users/${encodeURIComponent(userId)}/two-factor-setup-code`, {
        method: 'POST',
        headers: { 'X-Step-Up-Code': stepUpCode }
      })
  };
}

export type StaffApi = ReturnType<typeof createStaffApi>;

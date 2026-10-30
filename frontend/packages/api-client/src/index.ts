export interface ProblemDetails {
  type: string;
  title: string;
  status: number;
  detail: string;
  instance?: string;
  traceId?: string;
  error?: {
    code: string;
    message: string;
    details: Array<{ field?: string; message: string }>;
    traceId?: string;
  };
}

export interface AuthenticatedUser {
  id: string;
  email: string;
  phone?: string | null;
  fullName: string;
  roles: string[];
  permissions: string[];
  locale: 'en' | 'bn';
  twoFactorEnabled: boolean;
  sellerId?: string | null;
}

export interface AuthTokenEnvelope {
  accessToken: string;
  expiresIn: number;
  tokenType: string;
  user?: AuthenticatedUser;
  requiresTwoFactor?: boolean;
  twoFactorChallengeToken?: string;
}

export interface RegisterRequest {
  email: string;
  phone?: string;
  password: string;
  fullName: string;
  locale?: 'en' | 'bn';
}

export interface RegisterResponse {
  userId: string;
  status: string;
  verificationChannel: 'phone_otp' | 'email_otp';
  referenceId: string;
  expiresInSeconds: number;
}

export interface VerifyOtpRequest {
  recipient: string;
  code: string;
  type: 'PhoneVerify' | 'EmailVerify' | 'PasswordReset' | 'LoginOtp';
  referenceId: string;
}

export interface LoginRequest {
  identifier: string;
  password: string;
  totpCode?: string;
}

export interface BdLocationItem {
  id: string;
  parentId: string | null;
  level: 'division' | 'district' | 'upazila';
  nameEn: string;
  nameBn: string;
  shippingZone: 'inside_dhaka' | 'dhaka_suburb' | 'outside_dhaka';
  postalCodePrefix?: string | null;
}

export interface UserAddress {
  id: string;
  label: string;
  recipientName: string;
  phone: string;
  line1: string;
  line2?: string | null;
  area?: string | null;
  division: string;
  district: string;
  upazila: string;
  postalCode?: string | null;
  country: string;
  isDefaultShipping: boolean;
  isDefaultBilling: boolean;
  latitude?: number | null;
  longitude?: number | null;
}

export type UpsertAddressRequest = Omit<UserAddress, 'id' | 'country'>;

export interface ActiveSession {
  sessionId: string;
  familyId: string;
  ipAddress?: string | null;
  userAgent?: string | null;
  createdAtUtc: string;
  expiresAtUtc: string;
  isCurrent: boolean;
}

/**
 * Isolated closure holding the short-lived 15-minute access token strictly in JavaScript memory.
 * Phase 3 Acceptance Criterion #1:
 * "No token is ever readable by JavaScript except the short-lived access token in memory."
 * Never touches localStorage, sessionStorage, or non-httpOnly cookies.
 */
function createInMemoryTokenVault() {
  let accessTokenInMemory: string | null = null;
  let expiresAtEpochMs = 0;

  return {
    getToken(): string | null {
      if (!accessTokenInMemory) return null;
      if (expiresAtEpochMs > 0 && Date.now() >= expiresAtEpochMs) {
        accessTokenInMemory = null;
        expiresAtEpochMs = 0;
        return null;
      }
      return accessTokenInMemory;
    },
    setToken(token: string | null, expiresInSeconds = 900): void {
      if (!token) {
        accessTokenInMemory = null;
        expiresAtEpochMs = 0;
        return;
      }
      accessTokenInMemory = token;
      expiresAtEpochMs = Date.now() + expiresInSeconds * 1000;
    },
    clear(): void {
      accessTokenInMemory = null;
      expiresAtEpochMs = 0;
    }
  };
}

export const defaultTokenVault = createInMemoryTokenVault();

export interface MechWareApiClientConfig {
  baseUrl: string;
  getAccessToken?: () => Promise<string | null> | string | null;
  onUnauthorized?: () => void;
  autoRefreshOn401?: boolean;
}

export class MechWareApiClient {
  private readonly baseUrl: string;
  private readonly getAccessToken?: () => Promise<string | null> | string | null;
  private readonly onUnauthorized?: () => void;
  private readonly autoRefreshOn401: boolean;
  private refreshPromise: Promise<string | null> | null = null;

  constructor(config: MechWareApiClientConfig) {
    this.baseUrl = config.baseUrl.replace(/\/$/, '');
    this.getAccessToken = config.getAccessToken ?? (() => defaultTokenVault.getToken());
    this.onUnauthorized = config.onUnauthorized;
    this.autoRefreshOn401 = config.autoRefreshOn401 ?? true;
  }

  public async fetch<T>(path: string, options: RequestInit = {}, isRetry = false): Promise<T> {
    const cleanPath = path.replace(/^\//, '');
    const url = `${this.baseUrl}/${cleanPath}`;
    const headers = new Headers(options.headers);

    if (!headers.has('Content-Type') && !(options.body instanceof FormData)) {
      headers.set('Content-Type', 'application/json');
    }

    if (this.getAccessToken) {
      const token = await this.getAccessToken();
      if (token && !headers.has('Authorization')) {
        headers.set('Authorization', `Bearer ${token}`);
      }
    }

    const response = await fetch(url, {
      ...options,
      credentials: options.credentials ?? 'include',
      headers
    });

    if (
      response.status === 401 &&
      !isRetry &&
      this.autoRefreshOn401 &&
      !cleanPath.startsWith('api/v1/auth/login') &&
      !cleanPath.startsWith('api/v1/auth/refresh')
    ) {
      const refreshedToken = await this.tryRefreshAccessToken();
      if (refreshedToken) {
        const retryHeaders = new Headers(options.headers);
        retryHeaders.set('Authorization', `Bearer ${refreshedToken}`);
        return this.fetch<T>(path, { ...options, headers: retryHeaders }, true);
      }
      defaultTokenVault.clear();
      if (this.onUnauthorized) {
        this.onUnauthorized();
      }
    } else if (response.status === 401 && this.onUnauthorized) {
      this.onUnauthorized();
    }

    if (!response.ok) {
      const rawBody = await response.text();
      let problem: ProblemDetails;
      try {
        problem = JSON.parse(rawBody) as ProblemDetails;
      } catch {
        problem = {
          type: 'https://api.mechware.com.bd/errors/HTTP_ERROR',
          title: response.statusText || 'HTTP Error',
          status: response.status,
          detail: rawBody || `Request failed with status ${response.status}`
        };
      }
      throw problem;
    }

    if (response.status === 204) {
      return {} as T;
    }

    return response.json() as Promise<T>;
  }

  private async tryRefreshAccessToken(): Promise<string | null> {
    if (this.refreshPromise) {
      return this.refreshPromise;
    }

    this.refreshPromise = (async () => {
      try {
        const res = await fetch(`${this.baseUrl}/api/v1/auth/refresh`, {
          method: 'POST',
          credentials: 'include',
          headers: { 'Content-Type': 'application/json' }
        });
        if (!res.ok) {
          defaultTokenVault.clear();
          return null;
        }
        const payload = (await res.json()) as { data?: AuthTokenEnvelope };
        const nextToken = payload.data?.accessToken ?? null;
        if (nextToken) {
          defaultTokenVault.setToken(nextToken, payload.data?.expiresIn ?? 900);
        }
        return nextToken;
      } catch {
        defaultTokenVault.clear();
        return null;
      } finally {
        this.refreshPromise = null;
      }
    })();

    return this.refreshPromise;
  }

  /** Silent session restore from the httpOnly refresh cookie. True when a fresh access token is now in memory. */
  public async restoreSession(): Promise<boolean> {
    return (await this.tryRefreshAccessToken()) !== null;
  }

  public async getMe(): Promise<{ data: AuthenticatedUser }> {
    return this.fetch('/api/v1/users/me');
  }

  // Phase 3 Auth & Identity Endpoints
  public async register(payload: RegisterRequest): Promise<{ data: RegisterResponse }> {
    return this.fetch<{ data: RegisterResponse }>('/api/v1/auth/register', {
      method: 'POST',
      body: JSON.stringify(payload)
    });
  }

  public async verifyOtp(payload: VerifyOtpRequest): Promise<{ data: AuthTokenEnvelope }> {
    const res = await this.fetch<{ data: AuthTokenEnvelope }>('/api/v1/auth/verify-otp', {
      method: 'POST',
      body: JSON.stringify(payload)
    });
    if (res.data.accessToken) {
      defaultTokenVault.setToken(res.data.accessToken, res.data.expiresIn);
    }
    return res;
  }

  public async login(payload: LoginRequest): Promise<{ data: AuthTokenEnvelope }> {
    const res = await this.fetch<{ data: AuthTokenEnvelope }>('/api/v1/auth/login', {
      method: 'POST',
      body: JSON.stringify(payload)
    });
    if (res.data.accessToken) {
      defaultTokenVault.setToken(res.data.accessToken, res.data.expiresIn);
    }
    return res;
  }

  public async googleLogin(idToken: string, expectedEmail?: string, fullName?: string): Promise<{ data: AuthTokenEnvelope }> {
    const res = await this.fetch<{ data: AuthTokenEnvelope }>('/api/v1/auth/google', {
      method: 'POST',
      body: JSON.stringify({ idToken, expectedEmail, fullName })
    });
    if (res.data.accessToken) {
      defaultTokenVault.setToken(res.data.accessToken, res.data.expiresIn);
    }
    return res;
  }

  public async logout(): Promise<void> {
    try {
      await this.fetch('/api/v1/auth/logout', { method: 'POST' });
    } finally {
      defaultTokenVault.clear();
    }
  }

  public async forgotPassword(identifier: string): Promise<{ data: { referenceId: string; expiresInSeconds: number; message: string } }> {
    return this.fetch('/api/v1/auth/password/forgot', {
      method: 'POST',
      body: JSON.stringify({ identifier })
    });
  }

  public async resetPassword(recipient: string, referenceId: string, otpCode: string, newPassword: string): Promise<{ data: { passwordReset: boolean } }> {
    return this.fetch('/api/v1/auth/password/reset', {
      method: 'POST',
      body: JSON.stringify({ recipient, referenceId, otpCode, newPassword })
    });
  }

  public async setupTwoFactor(): Promise<{ data: { secret: string; otpAuthUri: string; recoveryCodes: string[] } }> {
    return this.fetch('/api/v1/auth/2fa/setup', { method: 'POST' });
  }

  public async verifyTwoFactor(code: string): Promise<{ data: { twoFactorEnabled: boolean } }> {
    return this.fetch('/api/v1/auth/2fa/verify', {
      method: 'POST',
      body: JSON.stringify({ code })
    });
  }

  public async getSessions(): Promise<{ data: ActiveSession[] }> {
    return this.fetch('/api/v1/auth/sessions');
  }

  public async revokeSessions(sessionId?: string, revokeAll = true): Promise<{ data: { revokedCount: number } }> {
    return this.fetch('/api/v1/auth/sessions/revoke', {
      method: 'POST',
      body: JSON.stringify({ sessionId, revokeAll })
    });
  }

  public async getAddresses(): Promise<{ data: UserAddress[] }> {
    return this.fetch('/api/v1/users/me/addresses');
  }

  public async createAddress(payload: UpsertAddressRequest): Promise<{ data: UserAddress }> {
    return this.fetch('/api/v1/users/me/addresses', {
      method: 'POST',
      body: JSON.stringify(payload)
    });
  }

  public async updateAddress(addressId: string, payload: UpsertAddressRequest): Promise<{ data: UserAddress }> {
    return this.fetch(`/api/v1/users/me/addresses/${addressId}`, {
      method: 'PUT',
      body: JSON.stringify(payload)
    });
  }

  public async deleteAddress(addressId: string): Promise<void> {
    await this.fetch(`/api/v1/users/me/addresses/${addressId}`, {
      method: 'DELETE'
    });
  }

  public async getBdLocations(level?: 'division' | 'district' | 'upazila', parentId?: string): Promise<{ data: BdLocationItem[] }> {
    const params = new URLSearchParams();
    if (level) params.set('level', level);
    if (parentId) params.set('parentId', parentId);
    const query = params.toString() ? `?${params.toString()}` : '';
    return this.fetch(`/api/v1/locations/bd${query}`);
  }

  public async exportGdprData(): Promise<{ data: unknown }> {
    return this.fetch('/api/v1/users/me/export');
  }

  public async anonymizeAccount(): Promise<{ data: { status: string; message: string } }> {
    const res = await this.fetch<{ data: { status: string; message: string } }>('/api/v1/users/me', {
      method: 'DELETE'
    });
    defaultTokenVault.clear();
    return res;
  }

  // Health checks
  public async getHealthLive(): Promise<string> {
    const res = await fetch(`${this.baseUrl}/health/live`);
    return res.text();
  }

  public async getHealthReady(): Promise<unknown> {
    const res = await fetch(`${this.baseUrl}/health/ready`);
    return res.json();
  }
}

export { createInMemoryTokenVault };

export * from "./companies-model";
export * from "./companies";

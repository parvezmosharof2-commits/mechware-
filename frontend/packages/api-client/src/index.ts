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

export interface MechWareApiClientConfig {
  baseUrl: string;
  getAccessToken?: () => Promise<string | null> | string | null;
  onUnauthorized?: () => void;
}

export class MechWareApiClient {
  private readonly baseUrl: string;
  private readonly getAccessToken?: () => Promise<string | null> | string | null;
  private readonly onUnauthorized?: () => void;

  constructor(config: MechWareApiClientConfig) {
    this.baseUrl = config.baseUrl.replace(/\/$/, '');
    this.getAccessToken = config.getAccessToken;
    this.onUnauthorized = config.onUnauthorized;
  }

  public async fetch<T>(path: string, options: RequestInit = {}): Promise<T> {
    const url = `${this.baseUrl}/${path.replace(/^\//, '')}`;
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
      headers
    });

    if (response.status === 401 && this.onUnauthorized) {
      this.onUnauthorized();
    }

    if (!response.ok) {
      let problem: ProblemDetails;
      try {
        problem = await response.json();
      } catch {
        problem = {
          type: 'https://api.mechware.com.bd/errors/HTTP_ERROR',
          title: response.statusText,
          status: response.status,
          detail: await response.text()
        };
      }
      throw problem;
    }

    if (response.status === 204) {
      return {} as T;
    }

    return response.json() as Promise<T>;
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

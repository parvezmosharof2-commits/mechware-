'use client';

import * as React from 'react';
import { createCompanyPricesApi } from '@mechware/api-client/src/company-prices';
import {
  asksKey,
  buildItemsQuery,
  buyerCompanies,
  chooseCompany,
  indexItems,
  type CompanyPriceItem,
  type CompanyPricesResponse,
  type PriceAsk
} from '@mechware/api-client/src/company-prices-model';
import { api, companiesApi } from './api';
import { useAuth } from './auth';

const pricesApi = createCompanyPricesApi(api);

export interface CompanyPricesState {
  /** 'idle' = not a company buyer (or nothing to ask); 'loading' = first answer pending; 'ready' = `items` is usable. */
  phase: 'idle' | 'loading' | 'ready';
  company: { id: string; name: string } | null;
  items: Map<string, CompanyPriceItem>;
  /** A newer question is in flight; `items` still holds the previous answer. */
  refreshing: boolean;
}

const IDLE: CompanyPricesState = { phase: 'idle', company: null, items: new Map(), refreshing: false };

/**
 * Memory only, per browser tab and per signed-in user (never localStorage, never a server cache): the company list and the
 * answers already received. A new user id drops everything.
 */
const session: {
  userId: string | null;
  company: Promise<{ id: string; name: string } | null> | null;
  answers: Map<string, CompanyPricesResponse>;
} = { userId: null, company: null, answers: new Map() };

function companyFor(userId: string) {
  if (session.userId !== userId) {
    session.userId = userId;
    session.company = null;
    session.answers = new Map();
  }
  session.company ??= companiesApi.listMine().then(
    (r) => {
      const chosen = chooseCompany(buyerCompanies(r.data));
      return chosen ? { id: chosen.company.id, name: chosen.company.legalName } : null;
    },
    () => {
      session.company = null; // retry on a later page; for now behave like a visitor
      return null;
    }
  );
  return session.company;
}

/**
 * D2.13: the signed-in company buyer's own prices for `asks`, fetched in the browser with the user's token after hydration.
 * Silent by design: a guest, a personal account, a 404/401 or a network failure all leave the state at 'idle' so the page
 * simply keeps the public price. Debounced and abortable; the answer is never written to any storage.
 */
export function useCompanyPrices(asks: readonly PriceAsk[], debounceMs = 400): CompanyPricesState {
  const { status, user } = useAuth();
  const userId = status === 'authenticated' ? (user?.id ?? null) : null;
  const query = buildItemsQuery(asks);
  const [state, setState] = React.useState<CompanyPricesState>(IDLE);

  React.useEffect(() => {
    if (!userId || !query) {
      setState((s) => (s.phase === 'idle' ? s : IDLE));
      return;
    }
    const controller = new AbortController();
    let live = true;
    const timer = window.setTimeout(async () => {
      try {
        const company = await companyFor(userId);
        if (!live) return;
        if (!company) return setState(IDLE);
        const key = asksKey(company.id, asks);
        const cached = session.answers.get(key);
        if (cached) return setState({ phase: 'ready', company, items: indexItems(cached), refreshing: false });
        setState((s) => (s.phase === 'ready' && s.company?.id === company.id ? { ...s, refreshing: true } : { ...IDLE, phase: 'loading', company }));
        const answer = await pricesApi.get(company.id, asks, controller.signal);
        if (!live) return;
        session.answers.set(key, answer);
        setState({ phase: 'ready', company, items: indexItems(answer), refreshing: false });
      } catch {
        // 401/404/network/abort: the public price stays as it is, no banner.
        if (live) setState(IDLE);
      }
    }, debounceMs);
    return () => {
      live = false;
      controller.abort();
      window.clearTimeout(timer);
    };
    // `query` is the stable form of `asks`.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [userId, query, debounceMs]);

  return state;
}

'use client';

import * as React from 'react';
import { quoteCompanies, type MyCompanyDto } from '@mechware/api-client';
import { companiesApi } from './api';
import { useAuth } from './auth';

export type QuoteAccessState =
  | { phase: 'loading' }
  /** A guest, or a person who is not a member of any company that can ask: they get the short "company accounts" note. */
  | { phase: 'anonymous' | 'none' }
  | { phase: 'ready'; companies: MyCompanyDto[] }
  | { phase: 'error' };

/**
 * Memory only, per tab and per signed-in user (never stored): the person's own company list. A new user id drops it.
 * The API still decides every request; this only decides which words and buttons the page shows.
 */
const session: { userId: string | null; mine: Promise<MyCompanyDto[]> | null } = { userId: null, mine: null };

function mineFor(userId: string): Promise<MyCompanyDto[]> {
  if (session.userId !== userId) {
    session.userId = userId;
    session.mine = null;
  }
  session.mine ??= companiesApi.listMine().then(
    (r) => r.data,
    (e) => {
      session.mine = null;
      throw e;
    }
  );
  return session.mine;
}

export function useQuoteAccess(): QuoteAccessState {
  const { status, user } = useAuth();
  const [state, setState] = React.useState<QuoteAccessState>({ phase: 'loading' });
  const userId = user?.id ?? null;

  React.useEffect(() => {
    if (status === 'loading') return setState({ phase: 'loading' });
    if (status === 'anonymous' || !userId) return setState({ phase: 'anonymous' });
    let current = true;
    setState({ phase: 'loading' });
    mineFor(userId).then(
      (mine) => {
        if (!current) return;
        const companies = quoteCompanies(mine);
        setState(companies.length > 0 ? { phase: 'ready', companies } : { phase: 'none' });
      },
      () => current && setState({ phase: 'error' })
    );
    return () => {
      current = false;
    };
  }, [status, userId]);

  return state;
}

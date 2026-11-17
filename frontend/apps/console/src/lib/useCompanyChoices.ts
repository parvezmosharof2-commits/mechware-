'use client';

import * as React from 'react';
import { orderChoices, paymentChoices, type CreditStatement, type OrderChoice, type PaymentChoice } from '@mechware/api-client';
import { creditBooksApi } from './creditBooks';

const PAGE = 200;

export interface CompanyChoices {
  loading: boolean;
  error: unknown;
  statement: CreditStatement | null;
  /** Orders named in the loaded statement lines; with `complete`, only those that still owe. */
  orders: OrderChoice[];
  payments: PaymentChoice[];
  /** True when every ledger line of the company was loaded, so the amounts owed are exact. */
  complete: boolean;
  reload: () => void;
}

/**
 * The staff API has no list of a company's open orders or payments, so the statement is read for them: the first 200 lines and, for a
 * long ledger, the last 200 (the recent orders are the ones that still owe).
 */
export function useCompanyChoices(companyId: string): CompanyChoices {
  const [state, setState] = React.useState<{ loading: boolean; error: unknown; statement: CreditStatement | null; lines: CreditStatement['lines']['items']; complete: boolean }>({ loading: true, error: null, statement: null, lines: [], complete: false });
  const [version, setVersion] = React.useState(0);

  React.useEffect(() => {
    let current = true;
    setState((s) => ({ ...s, loading: true, error: null }));
    (async () => {
      const first = (await creditBooksApi.statement(companyId, { from: '', to: '', page: 1 }, PAGE)).data;
      let lines = first.lines.items;
      const pages = Math.ceil(first.lines.totalCount / PAGE);
      if (pages > 1) {
        const last = (await creditBooksApi.statement(companyId, { from: '', to: '', page: pages }, PAGE)).data;
        lines = [...lines, ...last.lines.items.filter((l) => !lines.some((x) => x.seq === l.seq))];
      }
      return { statement: first, lines, complete: pages <= 1 };
    })().then(
      (r) => current && setState({ loading: false, error: null, ...r }),
      (error) => current && setState({ loading: false, error, statement: null, lines: [], complete: false })
    );
    return () => { current = false; };
  }, [companyId, version]);

  const reload = React.useCallback(() => setVersion((v) => v + 1), []);
  const all = React.useMemo(() => orderChoices(state.lines), [state.lines]);
  const orders = React.useMemo(() => (state.complete ? all.filter((o) => o.owedMinor > 0) : all), [all, state.complete]);
  const payments = React.useMemo(() => paymentChoices(state.lines), [state.lines]);
  return { loading: state.loading, error: state.error, statement: state.statement, orders, payments, complete: state.complete, reload };
}

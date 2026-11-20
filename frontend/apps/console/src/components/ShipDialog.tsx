'use client';

import * as React from 'react';
import { Button } from '@mechware/ui/src/components/Button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@mechware/ui/src/components/Dialog';
import { Input } from '@mechware/ui/src/components/Input';
import { TRACKING_URL_HELP, shipProblems, type ShipFields, type ShipProblems } from '@mechware/api-client';

export interface ShipDialogProps {
  orderNumber: string;
  busy: boolean;
  onClose: () => void;
  /** Sends the call; answers the server's field errors, or null when it went through or failed in some other way. */
  onSubmit: (fields: ShipFields) => Promise<ShipProblems | null>;
}

/** The ship form: every field is checked here before anything is sent. Loaded only when a ship starts. */
export default function ShipDialog({ orderNumber, busy, onClose, onSubmit }: ShipDialogProps) {
  const [f, setF] = React.useState<ShipFields>({ courierName: '', trackingNumber: '', trackingUrl: '' });
  const [problems, setProblems] = React.useState<ShipProblems>({});
  const refs = { courierName: React.useRef<HTMLInputElement>(null), trackingNumber: React.useRef<HTMLInputElement>(null), trackingUrl: React.useRef<HTMLInputElement>(null) };
  const set = (k: keyof ShipFields) => (e: React.ChangeEvent<HTMLInputElement>) => setF((prev) => ({ ...prev, [k]: e.target.value }));

  const focusFirst = (p: ShipProblems) => {
    const first = (['courierName', 'trackingNumber', 'trackingUrl'] as const).find((k) => p[k]);
    if (first) refs[first].current?.focus();
  };
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    const local = shipProblems(f);
    setProblems(local);
    if (Object.keys(local).length) return focusFirst(local);
    const server = await onSubmit(f);
    if (server && Object.keys(server).length) { setProblems(server); focusFirst(server); }
  };

  return (
    <Dialog open onOpenChange={(next) => { if (!next && !busy) onClose(); }}>
      <DialogContent>
        <form onSubmit={submit} noValidate className="space-y-4">
          <DialogHeader>
            <DialogTitle>Ship order {orderNumber}</DialogTitle>
            <DialogDescription>The buyer sees the courier and the tracking number on their order page. A shipped order cannot be cancelled, and this step cannot be undone.</DialogDescription>
          </DialogHeader>
          <Input ref={refs.courierName} label="Courier name (2 to 80 characters)" value={f.courierName} onChange={set('courierName')} error={problems.courierName} maxLength={120} autoComplete="off" required />
          <Input ref={refs.trackingNumber} label="Tracking number (letters, digits and dashes, 1 to 60)" value={f.trackingNumber} onChange={set('trackingNumber')} error={problems.trackingNumber} maxLength={80} autoComplete="off" required />
          <Input ref={refs.trackingUrl} label="Tracking link (optional)" type="url" value={f.trackingUrl} onChange={set('trackingUrl')} error={problems.trackingUrl} helperText={TRACKING_URL_HELP} maxLength={600} autoComplete="off" />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={busy}>Do not ship</Button>
            <Button type="submit" data-testid="ship-confirm" disabled={busy}>{busy ? 'Shipping…' : 'Mark as shipped'}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

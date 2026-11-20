'use client';

import { Button } from '@mechware/ui/src/components/Button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@mechware/ui/src/components/Dialog';
import { Textarea } from '@mechware/ui/src/components/Textarea';
import { StepUpDialog } from './StepUpDialog';

export interface CancelDialogsProps {
  step: 'reason' | 'code';
  orderNumber: string;
  reason: string;
  onReason: (text: string) => void;
  problem: string | null;
  busy: boolean;
  codeError: string | null;
  onContinue: () => void;
  onClose: () => void;
  onSubmit: (code: string) => Promise<void>;
}

/** The two steps of a staff cancel: the reason first (checked before a code is asked for), then the authenticator code. Loaded only when a cancel starts. */
export default function OrderCancelDialogs(p: CancelDialogsProps) {
  return (
    <>
      <Dialog open={p.step === 'reason'} onOpenChange={(next) => { if (!next && !p.busy) p.onClose(); }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Cancel order {p.orderNumber}?</DialogTitle>
            <DialogDescription>
              Its stock goes back to the warehouses it came from, a credit order&apos;s charge is reduced (money already paid goes on account), and money paid through a
              gateway waits in the refund queue. The buyer is not charged a cancellation fee. This cannot be undone. You will be asked for your authenticator code next.
            </DialogDescription>
          </DialogHeader>
          <Textarea label="Why is it cancelled? (5 to 500 characters)" value={p.reason} onChange={(e) => p.onReason(e.target.value)} error={p.problem ?? undefined} maxLength={500} rows={3} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={p.onClose}>Keep the order</Button>
            <Button type="button" variant="destructive" data-testid="cancel-continue" onClick={p.onContinue}>Continue to the authenticator code</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
      <StepUpDialog
        open={p.step === 'code'}
        title="Confirm the cancellation"
        description={`Cancelling order ${p.orderNumber}: ${p.reason.trim()}`}
        confirmLabel="Cancel the order"
        busy={p.busy}
        error={p.codeError}
        onCancel={p.onClose}
        onSubmit={p.onSubmit}
      />
    </>
  );
}

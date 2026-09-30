import { Dialog, DialogContent } from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';
import { TriangleAlert } from 'lucide-react';
import { cn } from '@/lib/utils';

type UnsavedChangesAction = 'save' | 'discard' | 'cancel';

const ACTION_WORDING = {
  close: {
    message: 'You have unsaved changes that will be lost if you close this card.',
    discard: 'Discard & Close',
    save: 'Save & Close',
  },
  navigate: {
    message: 'You have unsaved changes that will be lost if you navigate away.',
    discard: 'Discard Changes',
    save: 'Save & Navigate',
  },
  // The copy starts from whichever version the user keeps: the saved edits, or
  // the card as it was before them.
  duplicate: {
    message:
      'You have unsaved changes on this card. Save them first and the copy includes them; discard them and the copy starts from the card as it was.',
    discard: 'Discard & Duplicate',
    save: 'Save & Duplicate',
  },
} as const;

type UnsavedChangesDialogProps = {
  open: boolean;
  onAction: (action: UnsavedChangesAction) => void;
  actionType?: 'close' | 'navigate' | 'duplicate';
};

export function UnsavedChangesDialog({
  open,
  onAction,
  actionType = 'close',
}: UnsavedChangesDialogProps) {
  const wording = ACTION_WORDING[actionType];

  return (
    <Dialog
      open={open}
      onOpenChange={(nextOpen) => {
        if (!nextOpen) onAction('cancel');
      }}
    >
      <DialogContent
        className={cn(
          'flex flex-col gap-0 p-0',
          // The duplicate labels are longer; at 420px the three buttons overflow.
          actionType === 'duplicate' ? 'sm:max-w-[480px]' : 'sm:max-w-[420px]',
        )}
        showCloseButton={false}
      >
        <div className="px-6 py-5">
          <div className="mb-4 flex h-10 w-10 items-center justify-center rounded-full bg-accent/15">
            <TriangleAlert className="h-5 w-5 text-accent" />
          </div>
          <h2 className="text-base font-bold">Unsaved Changes</h2>
          <p className="mt-2 text-sm text-muted-foreground">{wording.message}</p>
        </div>
        <div className="flex flex-col-reverse gap-2 border-t px-6 py-4 sm:flex-row sm:justify-end">
          <Button
            variant="outline"
            onClick={() => onAction('cancel')}
            className="text-muted-foreground"
          >
            Keep Editing
          </Button>
          <Button
            variant="outline"
            onClick={() => onAction('discard')}
            className="border-destructive text-destructive hover:bg-destructive/10"
          >
            {wording.discard}
          </Button>
          <Button onClick={() => onAction('save')}>{wording.save}</Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}

export type { UnsavedChangesAction };

'use client'

import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import type { AdminTagsController } from '@/lib/admin/use-admin-tags'

interface AdminTagDialogsProps {
  controller: AdminTagsController
}

export function AdminTagDialogs({ controller }: AdminTagDialogsProps) {
  const {
    createOpen,
    setCreateOpen,
    editing,
    setEditing,
    deleting,
    setDeleting,
    createForm,
    updateForm,
    createMutation,
    updateMutation,
    deleteMutation,
  } = controller

  return (
    <>
      <Dialog open={createOpen} onOpenChange={setCreateOpen}>
        <DialogContent className="border-border bg-card">
          <DialogHeader>
            <DialogTitle>New tag</DialogTitle>
            <DialogDescription>Tag name must be unique.</DialogDescription>
          </DialogHeader>
          <form
            className="space-y-3"
            onSubmit={createForm.handleSubmit((values) => createMutation.mutate(values))}
          >
            <div className="space-y-1.5">
              <Label htmlFor="tag-create-name">Name</Label>
              <Input id="tag-create-name" {...createForm.register('name')} />
              {createForm.formState.errors.name ? (
                <p className="text-xs text-destructive">
                  {createForm.formState.errors.name.message}
                </p>
              ) : null}
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setCreateOpen(false)}>
                Cancel
              </Button>
              <Button type="submit" disabled={createMutation.isPending}>
                {createMutation.isPending ? 'Saving…' : 'Create'}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <Dialog open={editing !== null} onOpenChange={(open) => !open && setEditing(null)}>
        <DialogContent className="border-border bg-card">
          <DialogHeader>
            <DialogTitle>Edit tag</DialogTitle>
          </DialogHeader>
          <form
            className="space-y-3"
            onSubmit={updateForm.handleSubmit((values) => {
              if (editing) {
                updateMutation.mutate({ id: editing.id, name: values.name.trim() })
              }
            })}
          >
            <div className="space-y-1.5">
              <Label htmlFor="tag-edit-name">Name</Label>
              <Input id="tag-edit-name" {...updateForm.register('name')} />
              {updateForm.formState.errors.name ? (
                <p className="text-xs text-destructive">
                  {updateForm.formState.errors.name.message}
                </p>
              ) : null}
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setEditing(null)}>
                Cancel
              </Button>
              <Button type="submit" disabled={updateMutation.isPending}>
                {updateMutation.isPending ? 'Saving…' : 'Save'}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>

      <AlertDialog open={deleting !== null} onOpenChange={(open) => !open && setDeleting(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete tag?</AlertDialogTitle>
            <AlertDialogDescription>
              {deleting ? `“${deleting.name}” will be removed from the global tag list.` : null}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
              onClick={() => deleting && deleteMutation.mutate(deleting.id)}
            >
              Delete
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  )
}

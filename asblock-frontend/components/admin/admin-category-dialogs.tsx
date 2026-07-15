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
import { Textarea } from '@/components/ui/textarea'
import type { AdminCategoriesController } from '@/lib/admin/use-admin-categories'

function slugifyHint(name: string): string {
  return name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '')
}

interface AdminCategoryDialogsProps {
  controller: AdminCategoriesController
}

export function AdminCategoryDialogs({ controller }: AdminCategoryDialogsProps) {
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
    submitUpdate,
  } = controller

  return (
    <>
      <Dialog open={createOpen} onOpenChange={setCreateOpen}>
        <DialogContent className="border-border bg-card">
          <DialogHeader>
            <DialogTitle>New category</DialogTitle>
            <DialogDescription>Name, optional description, and URL slug.</DialogDescription>
          </DialogHeader>
          <form
            className="space-y-3"
            onSubmit={createForm.handleSubmit((values) => createMutation.mutate(values))}
          >
            <div className="space-y-1.5">
              <Label htmlFor="cat-create-name">Name</Label>
              <Input
                id="cat-create-name"
                {...createForm.register('name')}
                onBlur={(event) => {
                  const slug = createForm.getValues('slug')
                  if (!slug && event.target.value) {
                    createForm.setValue('slug', slugifyHint(event.target.value), {
                      shouldValidate: true,
                    })
                  }
                }}
              />
              {createForm.formState.errors.name ? (
                <p className="text-xs text-destructive">
                  {createForm.formState.errors.name.message}
                </p>
              ) : null}
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="cat-create-desc">Description</Label>
              <Textarea
                id="cat-create-desc"
                className="min-h-[4rem]"
                {...createForm.register('description')}
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="cat-create-slug">Slug</Label>
              <Input
                id="cat-create-slug"
                className="font-mono text-xs"
                {...createForm.register('slug')}
              />
              {createForm.formState.errors.slug ? (
                <p className="text-xs text-destructive">
                  {createForm.formState.errors.slug.message}
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
            <DialogTitle>Edit category</DialogTitle>
            <DialogDescription>Update fields and save.</DialogDescription>
          </DialogHeader>
          <form className="space-y-3" onSubmit={updateForm.handleSubmit(submitUpdate)}>
            <div className="space-y-1.5">
              <Label htmlFor="cat-edit-name">Name</Label>
              <Input id="cat-edit-name" {...updateForm.register('name')} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="cat-edit-desc">Description</Label>
              <Textarea
                id="cat-edit-desc"
                className="min-h-[4rem]"
                {...updateForm.register('description')}
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="cat-edit-slug">Slug</Label>
              <Input
                id="cat-edit-slug"
                className="font-mono text-xs"
                {...updateForm.register('slug')}
              />
              {updateForm.formState.errors.slug ? (
                <p className="text-xs text-destructive">
                  {updateForm.formState.errors.slug.message}
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
            <AlertDialogTitle>Delete category?</AlertDialogTitle>
            <AlertDialogDescription>
              {deleting
                ? `"${deleting.name}" will be removed. Assets still referencing it may break.`
                : null}
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

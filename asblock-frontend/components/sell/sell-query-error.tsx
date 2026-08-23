'use client'

import { AlertCircle, RotateCcw } from 'lucide-react'

import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'

interface SellQueryErrorProps {
  title: string
  onRetry: () => void
  retrying?: boolean
}

export function SellQueryError({ title, onRetry, retrying = false }: SellQueryErrorProps) {
  return (
    <Alert variant="destructive" className="border-destructive/40 bg-destructive/10 py-3">
      <AlertCircle className="h-4 w-4" aria-hidden />
      <AlertTitle className="text-sm">{title}</AlertTitle>
      <AlertDescription className="mt-2">
        <Button
          type="button"
          size="sm"
          variant="outline"
          className="border-destructive/40 bg-transparent text-destructive hover:bg-destructive/10"
          onClick={onRetry}
          disabled={retrying}
        >
          {retrying ? (
            <RotateCcw className="mr-2 size-3.5 animate-spin" aria-hidden />
          ) : (
            <RotateCcw className="mr-2 size-3.5" aria-hidden />
          )}
          Retry
        </Button>
      </AlertDescription>
    </Alert>
  )
}

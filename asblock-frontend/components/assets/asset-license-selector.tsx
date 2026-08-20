'use client'

import {
  Controller,
  type Control,
  type FieldErrors,
  type FieldPath,
  type FieldValues,
} from 'react-hook-form'
import Link from 'next/link'
import { ASSET_LICENSE_OPTIONS, type AssetLicenseCode } from '@/lib/assets/license-types'
import { Label } from '@/components/ui/label'
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group'
import { cn } from '@/lib/utils'

interface AssetLicenseSelectorProps<T extends FieldValues> {
  control: Control<T>
  name: FieldPath<T>
  errors?: FieldErrors<T>
  idPrefix: string
}

export function AssetLicenseSelector<T extends FieldValues>({
  control,
  name,
  errors,
  idPrefix,
}: AssetLicenseSelectorProps<T>) {
  const fieldError = errors?.[name]?.message as string | undefined

  return (
    <div className="space-y-2">
      <Label className="text-xs font-medium">License for buyers</Label>
      <Controller
        name={name}
        control={control}
        render={({ field }) => (
          <RadioGroup
            value={(field.value as AssetLicenseCode | undefined) ?? ''}
            onValueChange={field.onChange}
            className="grid gap-2"
          >
            {ASSET_LICENSE_OPTIONS.map((option) => {
              const inputId = `${idPrefix}-license-${option.code.toLowerCase()}`
              return (
                <label
                  key={option.code}
                  htmlFor={inputId}
                  className={cn(
                    'flex cursor-pointer items-start gap-3 rounded-lg border border-border bg-input/40 px-3 py-2.5 transition-colors',
                    field.value === option.code && 'border-primary/50 bg-primary/5',
                  )}
                >
                  <RadioGroupItem id={inputId} value={option.code} className="mt-0.5" />
                  <span className="min-w-0 space-y-0.5">
                    <span className="block text-sm font-medium text-foreground">
                      {option.label}
                    </span>
                    <span className="block text-[11px] text-muted-foreground leading-relaxed">
                      {option.summary}{' '}
                      <Link
                        href="/docs#licenses"
                        className="text-accent underline underline-offset-2 hover:text-foreground"
                        onClick={(e) => e.stopPropagation()}
                      >
                        Platform license summary
                      </Link>
                    </span>
                  </span>
                </label>
              )
            })}
          </RadioGroup>
        )}
      />
      {fieldError ? <p className="text-xs text-destructive">{fieldError}</p> : null}
    </div>
  )
}

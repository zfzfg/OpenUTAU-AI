import type { AutomationResult, ErrorCode } from './generated.js';

export class AutomationError extends Error {
  constructor(readonly code: ErrorCode | string, message: string, readonly details?: Record<string, unknown>) { super(message); }
}
export function failure(error: unknown): AutomationResult {
  if (error instanceof AutomationError) return {ok:false,error:{code:error.code as ErrorCode,message:error.message,...(error.details ? {details:error.details}: {})}};
  return {ok:false,error:{code:'APP_UNAVAILABLE',message:'The local OpenUtau connection could not complete. Read the operation journal before retrying a write.'}};
}
export function requireString(value: unknown, label: string): string {
  if(typeof value!=='string'||!value||value.length>4096) throw new AutomationError('INVALID_ARGUMENT',`${label} must be a nonempty string.`);
  return value;
}

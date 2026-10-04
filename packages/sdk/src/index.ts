export interface User { id: string; name: string; email: string; role: 'parent' | 'staff' }
export interface Student { id: string; name: string; grade: number }
export interface School { id: string; name: string; city: string }
export interface Balance { studentId: string; balanceMinor: number; currency: string }
export interface SchoolApplication { id: string; studentId: string; schoolId: string; academicYear: string; status: 'Submitted' | 'Approved' | 'Declined'; submittedAt: number }
export interface StaffApplication extends Omit<SchoolApplication,'studentId'> { userId: string; studentName: string; version: number }
export interface ApplicationInput { submissionId: string; studentId: string; schoolId: string; academicYear: string }
export interface RequestEvent { method: string; route: string; stage: 'signing' | 'sending' | 'complete' | 'error'; status?: number; code?: string; traceId?: string; elapsedMs?: number }
interface Context { signingKey: string; expiresAt: number; serverTime: number; user: User }
export class SchoolError extends Error {
  constructor(public readonly code: string, public readonly status = 0, public readonly traceId = '', public readonly retryAfter = 0) { super(code.replaceAll('_',' ')); this.name = 'SchoolError'; }
}
const encoder = new TextEncoder();
const hex = (bytes: ArrayBuffer) => Array.from(new Uint8Array(bytes),b => b.toString(16).padStart(2,'0')).join('');
const uuid = (id: string) => { if (!/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(id)) throw new SchoolError('VALIDATION_FAILED'); return id; };

/** One client per tab. No token/secret persistence, arbitrary URLs, or automatic write retries. */
export class SchoolClient {
  private key?: CryptoKey;
  private offset = 0;
  private observer?: (event: RequestEvent) => void;
  private user?: User;
  constructor(options: { onRequest?: (event: RequestEvent) => void } = {}) { this.observer = options.onRequest; }
  private emit(event: RequestEvent) { try { this.observer?.(event); } catch { /* UI observers cannot alter network behavior. */ } }
  private clear() { this.key = undefined; this.user = undefined; this.offset = 0; }
  private async accept(context: Context): Promise<User> {
    if (!context.user?.id || !Number.isFinite(context.serverTime) || !Number.isFinite(context.expiresAt)) throw new SchoolError('UNEXPECTED_RESPONSE');
    const bytes = Uint8Array.from(atob(context.signingKey), c => c.charCodeAt(0));
    if (bytes.length !== 32) throw new SchoolError('UNEXPECTED_RESPONSE');
    this.key = await crypto.subtle.importKey('raw',bytes,{name:'HMAC',hash:'SHA-256'},false,['sign']);
    this.offset = context.serverTime - Date.now(); this.user = context.user; return context.user;
  }
  async restore() { return this.accept(await this.request<Context>('POST','/session/context',{},false)); }
  async register(input: { email: string }) { return this.request<{message:string}>('POST','/auth/register',input,false); }
  async login(email: string,password: string) { this.clear(); return this.accept(await this.request<Context>('POST','/auth/login',{email,password},false)); }
  async verifyEmail(token: string,name: string,password: string) { return this.request<{message:string}>('POST','/auth/verify',{token,name,password},false); }
  async forgotPassword(email: string) { return this.request<{message:string}>('POST','/auth/forgot',{email},false); }
  async resetPassword(token: string,password: string) { return this.request<{message:string}>('POST','/auth/reset',{token,password},false); }
  async logout() { await this.request<void>('POST','/api/logout',{}); this.clear(); }
  me() { return this.request<User>('GET','/api/me'); }
  students() { return this.request<Student[]>('GET','/api/students'); }
  addStudent(input: Student) { uuid(input.id); return this.request<Student>('POST','/api/students',input); }
  balance(id: string) { return this.request<Balance>('GET',`/api/students/${uuid(id)}/balance`); }
  schools() { return this.request<School[]>('GET','/api/schools'); }
  applications() { return this.request<SchoolApplication[]>('GET','/api/applications'); }
  apply(input: ApplicationInput) { uuid(input.submissionId); uuid(input.studentId); return this.request<SchoolApplication>('POST','/api/applications',input); }
  staffApplications() { return this.request<StaffApplication[]>('GET','/api/staff/applications'); }
  decide(item: StaffApplication,status: 'Approved' | 'Declined') { return this.request<{message:string}>('POST',`/api/staff/applications/${uuid(item.userId)}/${uuid(item.id)}/decision`,{status,version:item.version}); }
  private async request<T>(method: string,route: string,input?: unknown,signed = true): Promise<T> {
    const path = '/api-proxy' + route;
    const serialized = input === undefined ? undefined : JSON.stringify(input);
    const bytes = encoder.encode(serialized ?? '');
    if (bytes.byteLength > 32768) throw new SchoolError('PAYLOAD_TOO_LARGE');
    const headers = new Headers({Accept:'application/json, application/problem+json'});
    if (serialized !== undefined) headers.set('Content-Type','application/json');
    const safeRoute = route.replace(/[a-f0-9-]{36}/gi,':id');
    const started = performance.now();
    if (signed) {
      if (!this.key) throw new SchoolError('SESSION_CONTEXT_REQUIRED');
      this.emit({method,route:safeRoute,stage:'signing'});
      const timestamp = String(Date.now() + this.offset); const requestId = crypto.randomUUID();
      const bodyHash = hex(await crypto.subtle.digest('SHA-256',bytes));
      const canonical = [method,path,timestamp,requestId,'main',bodyHash].join('\n');
      const signature = new Uint8Array(await crypto.subtle.sign('HMAC',this.key,encoder.encode(canonical)));
      headers.set('X-SC-App','main'); headers.set('X-SC-Session','cookie'); headers.set('X-SC-Timestamp',timestamp);
      headers.set('X-SC-Request-Id',requestId); headers.set('X-SC-Signature',btoa(String.fromCharCode(...signature)));
    }
    const controller = new AbortController(); const timer = setTimeout(() => controller.abort(),15000);
    try {
      this.emit({method,route:safeRoute,stage:'sending'});
      const response = await fetch(path,{method,headers,body:serialized,credentials:'same-origin',mode:'same-origin',redirect:'error',cache:'no-store',signal:controller.signal});
      const traceId = response.headers.get('X-Correlation-Id') ?? '';
      let result: unknown;
      if (response.status !== 204) {
        if (!response.headers.get('Content-Type')?.includes('json')) throw new SchoolError('UNEXPECTED_RESPONSE',response.status,traceId);
        result = await response.json();
      }
      if (!response.ok) {
        const problem = result as {code?:string};
        if (response.status === 401 && signed) this.clear();
        throw new SchoolError(problem?.code ?? 'UNEXPECTED_RESPONSE',response.status,traceId,Number(response.headers.get('Retry-After')) || 0);
      }
      this.emit({method,route:safeRoute,stage:'complete',status:response.status,traceId,elapsedMs:Math.round(performance.now()-started)});
      return result as T;
    } catch (error) {
      const safe = error instanceof SchoolError ? error : new SchoolError(controller.signal.aborted ? 'CLIENT_TIMEOUT' : error instanceof SyntaxError ? 'UNEXPECTED_RESPONSE' : 'NETWORK_ERROR');
      this.emit({method,route:safeRoute,stage:'error',status:safe.status,code:safe.code,traceId:safe.traceId,elapsedMs:Math.round(performance.now()-started)});
      throw safe;
    } finally { clearTimeout(timer); }
  }
}

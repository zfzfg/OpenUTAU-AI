import { createServer, type Server } from 'node:http';
import { randomBytes, randomUUID, timingSafeEqual } from 'node:crypto';
import { mkdir, writeFile, rm } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { AutomationError } from '../contracts/errors.js';

export interface QuestionContext {sessionId:string;turnId?:string;processGeneration:number;}
export interface PendingQuestion {requestId:string;context:QuestionContext;question:string;options?:{optionId:string;name:string}[];status:'pending'|'answered'|'cancelled';answer?:unknown;createdAt:number;}
const execFileAsync=promisify(execFile);
export async function privateWrite(path:string,data:unknown){
  await mkdir(dirname(path),{recursive:true,mode:0o700});await writeFile(path,JSON.stringify(data),{mode:0o600,flag:'wx'});
  if(process.platform==='win32'){
    const script="$ErrorActionPreference='Stop'; $p=$env:OPENUTAU_SECURITY_PATH; $u=[System.Security.Principal.WindowsIdentity]::GetCurrent().User; $a=[System.IO.File]::GetAccessControl($p); $a.SetAccessRuleProtection($true,$false); $a.SetAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new($u,'FullControl','Allow')); [System.IO.File]::SetAccessControl($p,$a)";
    await execFileAsync('powershell.exe',['-NoProfile','-NonInteractive','-EncodedCommand',Buffer.from(script,'utf16le').toString('base64')],{windowsHide:true,timeout:5000,env:{...process.env,OPENUTAU_SECURITY_PATH:path}});
  }
}
export class QuestionService {
  private server?:Server;private credential=randomBytes(32).toString('base64url');private refs=new Set<string>();private questions=new Map<string,PendingQuestion>();
  private sessions=new Map<string,()=>QuestionContext>();
  constructor(readonly notify:(method:string,params:Record<string,unknown>)=>void){}
  async start(){
    if(this.server)return;
    const server=createServer((req,res)=>{void(async()=>{
      try{
        if(req.headers.origin)throw new AutomationError('UNAUTHORIZED','Browser origins cannot access the question service.');
        const candidate=Buffer.from((req.headers.authorization||'').replace(/^Bearer /,''));const expected=Buffer.from(this.credential);
        if(candidate.length!==expected.length||!timingSafeEqual(candidate,expected))throw new AutomationError('UNAUTHORIZED','Invalid assistant question credential.');
        if(req.method!=='POST')throw new AutomationError('INVALID_ARGUMENT','Use a question service POST request.');
        let bytes=0;const chunks:Buffer[]=[];
        for await(const chunk of req){bytes+=chunk.length;if(bytes>65536)throw new AutomationError('LIMIT_EXCEEDED','Question request exceeds 64 KiB.');chunks.push(chunk);}
        const body=JSON.parse(Buffer.concat(chunks).toString('utf8'));
        const context=this.sessions.get(body.sessionId)?.();
        if(!context||body.processGeneration!==context.processGeneration)throw new AutomationError('UNAUTHORIZED','This helper session is no longer active.');
        let value:unknown;
        if(req.url==='/ask'){
          if(!context.turnId)throw new AutomationError('INVALID_ARGUMENT','Questions require an active turn.');
          if(typeof body.question!=='string'||!body.question||body.question.length>8192)throw new AutomationError('INVALID_ARGUMENT','A bounded question is required.');
          if([...this.questions.values()].filter(x=>x.status==='pending'&&x.context.sessionId===body.sessionId).length>=8)throw new AutomationError('LIMIT_EXCEEDED','This turn already has eight unanswered questions.');
          if(body.options!==undefined&&(!Array.isArray(body.options)||body.options.length>16||body.options.some((x:any)=>typeof x.optionId!=='string'||typeof x.name!=='string')))throw new AutomationError('INVALID_ARGUMENT','Question options must have optionId and name.');
          for(const [id,entry] of this.questions){if(entry.status!=='pending'&&entry.createdAt<Date.now()-15*60*1000)this.questions.delete(id);}
          if(this.questions.size>=512)throw new AutomationError('LIMIT_EXCEEDED','Question history reached its bounded capacity; reconnect the native session.');
          const question:PendingQuestion={requestId:randomUUID(),context,question:body.question,options:body.options,status:'pending',createdAt:Date.now()};
          this.questions.set(question.requestId,question);
          this.notify('questionRequested',{...context,eventId:randomUUID(),requestId:question.requestId,title:body.title||'Question',question:question.question,options:question.options});
          value={ok:true,questionId:question.requestId,status:'pending'};
        }else if(req.url==='/read'){
          const question=this.questions.get(body.questionId);
          if(!question||question.context.sessionId!==context.sessionId||question.context.processGeneration!==context.processGeneration)throw new AutomationError('ENTITY_NOT_FOUND','Question does not belong to this session.');
          const timeoutMs=Math.min(30000,Math.max(0,Number(body.timeoutMs)||0));const until=Date.now()+timeoutMs;
          while(question.status==='pending'&&Date.now()<until&&!req.destroyed)await new Promise(resolve=>setTimeout(resolve,50));
          value={ok:true,questionId:question.requestId,status:question.status,...(question.status==='answered'?{answer:question.answer}:{})};
        }else throw new AutomationError('INVALID_ARGUMENT','Unknown assistant question endpoint.');
        res.writeHead(200,{'content-type':'application/json'});res.end(JSON.stringify(value));
      }catch(error){res.writeHead(error instanceof AutomationError&&error.code==='UNAUTHORIZED'?401:400,{'content-type':'application/json'});res.end(JSON.stringify({ok:false,error:{code:error instanceof AutomationError?error.code:'INVALID_ARGUMENT',message:error instanceof AutomationError?error.message:'Invalid question request.'}}));}
    })();});
    await new Promise<void>((resolve,reject)=>{server.once('error',reject);server.listen(0,'127.0.0.1',resolve);});this.server=server;
  }
  async bindSession(sessionId:string,processGeneration:number,grantRef:string,current:()=>QuestionContext):Promise<string>{
    await this.start();this.sessions.set(sessionId,current);
    const address=this.server!.address();if(!address||typeof address==='string')throw Error('Question endpoint is unavailable.');
    const path=join(dirname(grantRef),`assistant-helper-${randomUUID()}.json`);
    await privateWrite(path,{url:`http://127.0.0.1:${address.port}`,credential:this.credential,sessionId,processGeneration});this.refs.add(path);return path;
  }
  respond(params:Record<string,any>){
    const question=this.questions.get(params.requestId);
    if(!question||question.status!=='pending'||question.context.sessionId!==params.sessionId||question.context.turnId!==params.turnId||question.context.processGeneration!==params.processGeneration)throw new AutomationError('REQUEST_STALE','This question is no longer pending in this turn.');
    if(question.options?.length){if(!question.options.some(x=>x.optionId===params.optionId))throw new AutomationError('INVALID_ARGUMENT','Select an offered optionId.');question.answer={optionId:params.optionId,...(typeof params.answer==='string'?{text:params.answer}:{})};}
    else{if(typeof params.answer!=='string'||params.answer.length>16384)throw new AutomationError('INVALID_ARGUMENT','Provide a bounded text answer.');question.answer=params.answer;}
    question.status='answered';this.notify('requestWithdrawn',{...question.context,eventId:randomUUID(),requestId:question.requestId,reason:'answered'});return {accepted:true};
  }
  withdraw(sessionId:string,reason:string){for(const question of this.questions.values()){if(question.context.sessionId===sessionId&&question.status==='pending'){question.status='cancelled';this.notify('requestWithdrawn',{...question.context,eventId:randomUUID(),requestId:question.requestId,reason});}}}
  hasPending(sessionId:string){return [...this.questions.values()].some(x=>x.context.sessionId===sessionId&&x.status==='pending');}
  unbind(sessionId:string){this.withdraw(sessionId,'disposed');this.sessions.delete(sessionId);for(const [id,question] of this.questions){if(question.context.sessionId===sessionId)this.questions.delete(id);}}
  async dispose(){for(const id of this.sessions.keys())this.unbind(id);if(this.server){this.server.closeAllConnections();await new Promise<void>(resolve=>this.server!.close(()=>resolve()));this.server=undefined;}await Promise.all([...this.refs].map(path=>rm(path,{force:true})));this.refs.clear();}
}

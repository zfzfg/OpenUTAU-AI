import { randomUUID } from 'node:crypto';
import { lstat } from 'node:fs/promises';
import { AutomationError } from '../contracts/errors.js';
import type { AccessGrant, AutomationResult } from '../contracts/generated.js';
import { discover, nativeGrant, type ReachableInstance } from './discovery.js';

export class EditorClient {
  private connections=new Map<string, AccessGrant>();
  private nativeCache?:{grant:AccessGrant;fingerprint:string};
  readonly clientId:string;
  constructor(readonly env:NodeJS.ProcessEnv=process.env){this.clientId=env.OPENUTAU_CLIENT_ID||`mcp_${randomUUID()}`;}
  async instances():Promise<AutomationResult>{
    if(this.env.OPENUTAU_ACCESS_GRANT_REF){const grant=await this.cachedNativeGrant();return {ok:true,instances:[{instanceId:grant.instanceId,url:grant.url,apiVersion:'1',ready:true,native:true}]};}
    return {ok:true,instances:(await discover(this.env)).map(({descriptor:_,...item})=>item)};
  }
  private async cachedNativeGrant():Promise<AccessGrant>{
    const path=this.env.OPENUTAU_ACCESS_GRANT_REF!;
    try{
      const info=await lstat(path);
      if(!info.isFile()||info.isSymbolicLink()||process.platform!=='win32'&&(info.mode&0o077)!==0)throw new AutomationError('UNAUTHORIZED','The native grant reference is no longer protected.');
      const fingerprint=`${info.dev}:${info.ino}:${info.size}:${info.mtimeMs}:${info.ctimeMs}`;
      if(!this.nativeCache||this.nativeCache.fingerprint!==fingerprint)this.nativeCache={grant:await nativeGrant(path,this.env),fingerprint};
      const grant=this.nativeCache.grant;
      if(grant.expiresAt&&Date.parse(grant.expiresAt)<=Date.now())throw new AutomationError('UNAUTHORIZED','The native grant expired. Reconnect in OpenUtau.');
      return grant;
    }catch(error){if(error instanceof AutomationError)throw error;throw new AutomationError('UNAUTHORIZED','The native session grant is missing or invalid. Reconnect in OpenUtau.');}
  }
  private async connection(args:Record<string,unknown>):Promise<AccessGrant>{
    if(this.env.OPENUTAU_ACCESS_GRANT_REF){
      const grant=await this.cachedNativeGrant();
      if(args.instanceId&&args.instanceId!==grant.instanceId)throw new AutomationError('UNAUTHORIZED','This session cannot switch editor instances.');
      if(args.projectSessionId&&args.projectSessionId!==grant.projectSessionId)throw new AutomationError('PROJECT_CHANGED','Reconnect and read the new project session.');
      return grant;
    }
    const target=args.instanceId||this.env.OPENUTAU_INSTANCE_ID;
    const existing=typeof target==='string'?this.connections.get(target):undefined;
    if(existing){
      if(args.projectSessionId&&args.projectSessionId!==existing.projectSessionId)throw new AutomationError('PROJECT_CHANGED','Read the explicitly selected current project before preparing another write.');
      if(!existing.expiresAt||Date.parse(existing.expiresAt)>Date.now())return existing;
      this.connections.delete(String(target));
    }
    const instances=await discover(this.env);
    if(instances.length===0)throw new AutomationError('APP_NOT_RUNNING','OpenUtau is not running or its automation service is unavailable.');
    let selected:ReachableInstance|undefined;
    if(target){selected=instances.find(x=>x.instanceId===target);if(!selected)throw new AutomationError('APP_UNAVAILABLE','The selected OpenUtau instance is no longer reachable.');}
    else {if(instances.length!==1)throw new AutomationError('INSTANCE_AMBIGUOUS','Choose an instanceId from openutau_list_instances.');selected=instances[0];}
    const cached=this.connections.get(selected.instanceId);
    // An existing project binding is never silently moved to a different project for a write.
    if(cached&&(!args.projectSessionId||cached.projectSessionId===args.projectSessionId))return cached;
    const response=await fetch(`${selected.url}/automation/v1/connect`,{method:'POST',headers:{'content-type':'application/json',authorization:`Bearer ${selected.descriptor.token}`},body:JSON.stringify({clientId:this.clientId,...(args.projectSessionId ? {projectSessionId:args.projectSessionId}: {})}),signal:AbortSignal.timeout(5000),redirect:'error'});
    const value=await response.json() as Record<string,unknown>;
    if(!response.ok||value.ok===false)throw new AutomationError(String((value.error as Record<string,unknown>)?.code||'UNAUTHORIZED'),String((value.error as Record<string,unknown>)?.message||'The editor refused the connection.'));
    const data=(value.grant||value) as Record<string,unknown>;
    if(typeof data.credential!=='string'||typeof data.projectSessionId!=='string')throw new AutomationError('PROTOCOL_MISMATCH','Invalid editor connection grant.');
    const grant={...data,url:selected.url,instanceId:selected.instanceId} as unknown as AccessGrant;
    this.connections.set(selected.instanceId,grant);return grant;
  }
  async invoke(operation:string,args:Record<string,unknown>,signal?:AbortSignal,rebinding=false):Promise<AutomationResult>{
    if(operation==='list_instances')return this.instances();
    const grant=await this.connection(args);
    const request={apiVersion:'1',requestId:randomUUID(),clientId:grant.clientId||this.clientId,operation,arguments:args};
    const payload=JSON.stringify(request);
    if(Buffer.byteLength(payload)>2*1024*1024)throw new AutomationError('LIMIT_EXCEEDED','Request exceeds 2 MiB; divide it into smaller musical batches.');
    try {
      const response=await fetch(`${grant.url}/automation/v1/invoke`,{method:'POST',headers:{'content-type':'application/json',authorization:`Bearer ${grant.credential}`},body:payload,redirect:'error',signal:signal?AbortSignal.any([signal,AbortSignal.timeout(35000)]):AbortSignal.timeout(35000)});
      const text=await response.text();
      if(Buffer.byteLength(text)>8*1024*1024)throw new AutomationError('LIMIT_EXCEEDED','Editor result exceeds 8 MiB; use paginated reads.');
      const result=JSON.parse(text) as AutomationResult;
      if(typeof result.ok!=='boolean')throw new AutomationError('PROTOCOL_MISMATCH','The editor returned an invalid result envelope.');
      if(operation==='get_project'&&result.error?.code==='PROJECT_CHANGED'&&!rebinding&&!this.env.OPENUTAU_ACCESS_GRANT_REF){
        this.connections.delete(grant.instanceId);
        const {projectSessionId:_,...freshArgs}=args;
        return this.invoke(operation,{...freshArgs,instanceId:grant.instanceId},signal,true);
      }
      if(result.ok&&typeof result.projectSessionId==='string'&&['new_project','open_project'].includes(operation))this.connections.delete(grant.instanceId);
      return result;
    }catch(error){
      if(error instanceof AutomationError)throw error;
      throw new AutomationError(args.operationId?'OPERATION_STATE_UNKNOWN':'APP_UNAVAILABLE',args.operationId?'The connection ended after submitting the operation. Query openutau_get_operation with the same operationId before retrying.':'The editor connection is unavailable.',args.operationId?{operationId:args.operationId}:undefined);
    }
  }
}

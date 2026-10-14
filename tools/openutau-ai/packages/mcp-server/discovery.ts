import { readFile, readdir, lstat, realpath } from 'node:fs/promises';
import { homedir } from 'node:os';
import { join, isAbsolute, dirname } from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { AutomationError } from '../contracts/errors.js';
import type { InstanceDescriptor, AccessGrant } from '../contracts/generated.js';

const execFileAsync = promisify(execFile);
export function runtimeDirectory(env: NodeJS.ProcessEnv = process.env): string {
  if (env.OPENUTAU_AUTOMATION_DIR) {
    if(!isAbsolute(env.OPENUTAU_AUTOMATION_DIR))throw new AutomationError('UNAUTHORIZED','Runtime directory must be absolute.');
    return env.OPENUTAU_AUTOMATION_DIR;
  }
  return process.platform==='win32' ? join(env.LOCALAPPDATA || join(homedir(),'AppData','Local'),'OpenUtau','automation') : join(env.XDG_RUNTIME_DIR || env.XDG_DATA_HOME || join(homedir(),'.local','share'),'OpenUtau','automation');
}
export function loopbackUrl(value: unknown): string {
  if(typeof value!=='string')throw new AutomationError('UNAUTHORIZED','A local automation URL is required.');
  let url:URL;
  try{url=new URL(value);}catch{throw new AutomationError('UNAUTHORIZED','Invalid local automation URL.');}
  if(url.protocol!=='http:' || url.hostname!=='127.0.0.1'||url.username||url.password||url.search||url.hash||(url.pathname!=='/'&&url.pathname!==''))throw new AutomationError('UNAUTHORIZED','Automation must use a plain IPv4 loopback HTTP endpoint.');
  return url.origin;
}
export async function assertProtected(path: string | string[]): Promise<void> {
  const paths=Array.isArray(path)?path:[path];
  for(const item of paths){
    if(!isAbsolute(item))throw new AutomationError('UNAUTHORIZED','Credential references must be absolute paths.');
    const info=await lstat(item);
    if(info.isSymbolicLink() || !(info.isFile()||info.isDirectory()))throw new AutomationError('UNAUTHORIZED','Credential storage cannot be a link or special file.');
    if(process.platform!=='win32'&&((info.mode & 0o077)!==0 || (process.getuid && info.uid!==process.getuid())))throw new AutomationError('UNAUTHORIZED','Credential storage must be owned by this user and private (0600/0700).');
  }
  if(process.platform!=='win32')return;
  // Check actual DACLs, not the uninformative Windows stat mode bits. No credential contents enter this process.
  const script="$ErrorActionPreference='Stop'; $u=[System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value; foreach($p in $env:OPENUTAU_SECURITY_PATHS.Split([char]10)){if([System.IO.Directory]::Exists($p)){$a=[System.IO.Directory]::GetAccessControl($p)}else{$a=[System.IO.File]::GetAccessControl($p)}; foreach($r in $a.GetAccessRules($true,$true,[System.Security.Principal.SecurityIdentifier])){if($r.AccessControlType -eq 'Allow' -and ($r.FileSystemRights.value__ -band 1) -ne 0 -and $r.IdentityReference.Value -notin @($u,'S-1-5-18','S-1-5-32-544')){exit 2}}}; exit 0";
  try{await execFileAsync('powershell.exe',['-NoProfile','-NonInteractive','-EncodedCommand',Buffer.from(script,'utf16le').toString('base64')],{windowsHide:true,timeout:5000,env:{...process.env,OPENUTAU_SECURITY_PATHS:paths.join('\n')},maxBuffer:4096});}
  catch{throw new AutomationError('UNAUTHORIZED','Credential storage is readable by another Windows principal; restrict its ACL.');}
}
export async function protectedJson(path: string): Promise<Record<string, unknown>> {
  await assertProtected([dirname(path),path]);
  const info=await lstat(path);
  if(info.size>65536)throw new AutomationError('UNAUTHORIZED','Credential reference is oversized.');
  const data=JSON.parse(await readFile(await realpath(path),'utf8'));
  if(!data||typeof data!=='object'||Array.isArray(data))throw new AutomationError('UNAUTHORIZED','Invalid credential document.');
  return data;
}
export async function nativeGrant(path: string, env: NodeJS.ProcessEnv = process.env): Promise<AccessGrant> {
  try {
    const doc=await protectedJson(path);
    if(typeof doc.credential!=='string'||doc.credential.length<16||typeof doc.instanceId!=='string'||typeof doc.projectSessionId!=='string')throw Error();
    if(env.OPENUTAU_INSTANCE_ID && doc.instanceId!==env.OPENUTAU_INSTANCE_ID)throw Error();
    if(doc.expiresAt && (!Number.isFinite(Date.parse(String(doc.expiresAt)))||Date.parse(String(doc.expiresAt))<=Date.now()))throw Error();
    return {...doc,url:loopbackUrl(doc.url)} as unknown as AccessGrant;
  }catch(error){if(error instanceof AutomationError)throw error;throw new AutomationError('UNAUTHORIZED','The native session grant is missing, invalid or expired. Reconnect in OpenUtau.');}
}
export type ReachableInstance=Omit<InstanceDescriptor,'token'> & {ready:boolean; descriptor:InstanceDescriptor};
export async function discover(env: NodeJS.ProcessEnv = process.env): Promise<ReachableInstance[]> {
  const folder=runtimeDirectory(env);
  let names:string[];
  try{await assertProtected(folder);names=(await readdir(folder)).filter(x=>x.endsWith('.json'));}
  catch(error){if((error as NodeJS.ErrnoException).code==='ENOENT')return [];throw error;}
  const descriptors:InstanceDescriptor[]=[];
  for(const name of names) {
    try{
      const doc=await protectedJson(join(folder,name));
      if(doc.apiVersion!=='1'||typeof doc.instanceId!=='string'||typeof doc.pid!=='number'||typeof doc.startedAt!=='string'||typeof doc.token!=='string'||doc.token.length<16)continue;
      descriptors.push({...doc,url:loopbackUrl(doc.url)} as unknown as InstanceDescriptor);
    }catch{ /* Invalid and untrusted descriptors are never used or deleted. */ }
  }
  const duplicates = new Set(descriptors.filter((d,i)=>descriptors.findIndex(x=>x.instanceId===d.instanceId)!==i).map(x=>x.instanceId));
  const found=await Promise.all(descriptors.filter(d=>!duplicates.has(d.instanceId)).map(async descriptor=>{
    try{
      const response=await fetch(`${descriptor.url}/automation/v1/health`,{signal:AbortSignal.timeout(1500),redirect:'error'});
      if(!response.ok)return null;
      const health=await response.json() as Record<string,unknown>;
      if(health.instanceId!==descriptor.instanceId||health.startedAt!==descriptor.startedAt||health.apiVersion!=='1')return null;
      const {token:_,...publicInstance}=descriptor;
      return {...publicInstance,ready:health.ready!==false,descriptor};
    }catch{return null;}
  }));
  return found.filter((x):x is ReachableInstance=>x!==null);
}

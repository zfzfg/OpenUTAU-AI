import { StringDecoder } from 'node:string_decoder';
import type { Readable, Writable } from 'node:stream';
import { AutomationError } from '../contracts/errors.js';

export interface RpcFrame {jsonrpc:'2.0';id?:string|number|null;method?:string;params?:Record<string,unknown>;result?:unknown;error?:{code:number;message:string;data?:unknown};}
export class JsonLineReader {
  private decoder=new StringDecoder('utf8'); private buffer=''; private bytes=0; private failed=false;
  constructor(input:Readable,readonly frame:(value:RpcFrame)=>void,readonly fail:(error:Error)=>void,readonly end:()=>void,readonly limit=2*1024*1024){
    input.on('data',(chunk:Buffer)=>this.push(chunk));
    input.once('end',()=>{if(this.failed)return;this.buffer+=this.decoder.end();if(this.buffer.trim())this.fail(new AutomationError('PROTOCOL_MISMATCH','Incomplete JSON-RPC frame at EOF.'));this.end();});
    input.once('error',(error:Error)=>this.fail(error));
  }
  private push(chunk:Buffer){
    if(this.failed)return;
    this.bytes+=chunk.length;
    this.buffer+=this.decoder.write(chunk);
    try {
      let index:number;
      while((index=this.buffer.indexOf('\n'))>=0){
        const line=this.buffer.slice(0,index).replace(/\r$/,'');this.buffer=this.buffer.slice(index+1);this.bytes=Buffer.byteLength(this.buffer);
        if(Buffer.byteLength(line)>this.limit)throw new AutomationError('LIMIT_EXCEEDED','JSON-RPC frame exceeds 2 MiB.');
        if(!line.trim())continue;
        const value=JSON.parse(line);
        if(!value||typeof value!=='object'||value.jsonrpc!=='2.0'||(value.method!==undefined&&typeof value.method!=='string')||(value.id!==undefined&&value.id!==null&&typeof value.id!=='number'&&typeof value.id!=='string'))throw new AutomationError('PROTOCOL_MISMATCH','Invalid JSON-RPC frame.');
        this.frame(value);
      }
      if(this.bytes>this.limit)throw new AutomationError('LIMIT_EXCEEDED','Unterminated JSON-RPC frame exceeds 2 MiB.');
    }catch(error){this.failed=true;this.fail(error instanceof Error?error:new Error('Invalid JSON-RPC frame.'));}
  }
}
export class RpcPeer {
  private nextId=0; private pending=new Map<string|number,{resolve:(value:any)=>void;reject:(error:Error)=>void;timer?:NodeJS.Timeout}>();
  private ended=false;
  onNotification:(method:string,params:Record<string,any>)=>void=()=>{};
  onRequest:(method:string,params:Record<string,any>)=>Promise<unknown>=async()=>{throw new AutomationError('UNSUPPORTED_CAPABILITY','This client does not provide that ACP method.');};
  onEnd:(error?:Error)=>void=()=>{};
  constructor(readonly input:Readable,readonly output:Writable){new JsonLineReader(input,frame=>{void this.receive(frame);},error=>this.close(error),()=>this.close(new AutomationError('PROTOCOL_MISMATCH','The ACP process closed its output.')));}
  private async receive(frame:RpcFrame){
    if(this.ended)return;
    if(frame.method){
      if(frame.id!==undefined&&frame.id!==null){
        try{const result=await this.onRequest(frame.method,frame.params||{});this.send({jsonrpc:'2.0',id:frame.id,result});}
        catch(error){this.send({jsonrpc:'2.0',id:frame.id,error:{code:-32000,message:error instanceof AutomationError?error.message:'The client request failed.',data:{code:error instanceof AutomationError?error.code:'PROTOCOL_MISMATCH'}}});}
      }else this.onNotification(frame.method,frame.params||{});
      return;
    }
    if(frame.id===undefined||frame.id===null)return;
    const pending=this.pending.get(frame.id);if(!pending)return;
    this.pending.delete(frame.id);if(pending.timer)clearTimeout(pending.timer);
    if(frame.error)pending.reject(new AutomationError(String((frame.error.data as any)?.code||'PROVIDER_ERROR'),frame.error.message));else pending.resolve(frame.result);
  }
  send(frame:RpcFrame){if(this.ended)throw new AutomationError('APP_UNAVAILABLE','The ACP stream is closed.');const data=JSON.stringify(frame)+'\n';if(Buffer.byteLength(data)>2*1024*1024)throw new AutomationError('LIMIT_EXCEEDED','JSON-RPC frame exceeds 2 MiB.');this.output.write(data);}
  request(method:string,params:Record<string,unknown>,timeoutMs=15000):Promise<any>{
    if(this.ended)return Promise.reject(new AutomationError('APP_UNAVAILABLE','The ACP stream is closed.'));
    const id=++this.nextId;
    return new Promise((resolve,reject)=>{
      const timer=timeoutMs?setTimeout(()=>{this.pending.delete(id);reject(new AutomationError('PROVIDER_TIMEOUT','The ACP operation timed out.'));},timeoutMs):undefined;
      this.pending.set(id,{resolve,reject,timer});
      try{this.send({jsonrpc:'2.0',id,method,params});}catch(error){if(timer)clearTimeout(timer);this.pending.delete(id);reject(error);}
    });
  }
  notify(method:string,params:Record<string,unknown>){this.send({jsonrpc:'2.0',method,params});}
  close(error:Error=new AutomationError('CANCELLED','The ACP peer was disposed.')){
    if(this.ended)return;this.ended=true;
    for(const entry of this.pending.values()){if(entry.timer)clearTimeout(entry.timer);entry.reject(error);}this.pending.clear();this.onEnd(error);
  }
}

"""Loopback-only synthetic SSH/SFTP fixture. Never included in the release."""
import errno, json, os, socket, stat, sys, threading, time
from pathlib import Path
import paramiko

base=Path(sys.argv[1]).resolve(); base.mkdir(parents=True,exist_ok=True)
root=base/'remote'; root.mkdir(exist_ok=True)
key=paramiko.RSAKey.generate(2048); alternate=paramiko.RSAKey.generate(2048)
userkey=paramiko.RSAKey.generate(2048); userkey.write_private_key_file(str(base/'client.pem'))
userkey.write_private_key_file(str(base/'client-encrypted.pem'),password='fixture-key-passphrase')
lock=threading.Lock()
def log(message):
    with lock:
        with (base/'events.txt').open('a',encoding='utf-8') as f: f.write(message+'\n')
def resolve(path):
    target=(root/path.lstrip('/')).resolve()
    if not target.is_relative_to(root): raise PermissionError(errno.EACCES,'Outside fixture root')
    return target
def status(error): return paramiko.SFTPServer.convert_errno(error.errno or errno.EIO)

class Files(paramiko.SFTPServerInterface):
    def canonicalize(self,path):
        return '/'+resolve(path).relative_to(root).as_posix().removeprefix('.') if path not in ('','.','/') else '/'
    def list_folder(self,path):
        if path=='/denied': return paramiko.SFTP_PERMISSION_DENIED
        try:
            result=[]
            for child in resolve(path).iterdir():
                a=paramiko.SFTPAttributes.from_stat(child.stat()); a.filename=child.name; result.append(a)
            if path=='/special':
                a=paramiko.SFTPAttributes();a.filename='bad\nname';a.st_mode=stat.S_IFREG|0o644;a.st_size=0;result.append(a)
            return result
        except OSError as e:return status(e)
    def stat(self,path):
        try:return paramiko.SFTPAttributes.from_stat(resolve(path).stat())
        except OSError as e:return status(e)
    lstat=stat
    def open(self,path,flags,attr):
        try:
            fd=os.open(resolve(path),flags|os.O_BINARY,0o600)
            file=os.fdopen(fd,'r+b' if flags&os.O_RDWR else 'wb' if flags&os.O_WRONLY else 'rb',buffering=0)
            handle=paramiko.SFTPHandle(flags)
            if flags&os.O_WRONLY or flags&os.O_RDWR:handle.writefile=file
            if not flags&os.O_WRONLY:handle.readfile=file
            return handle
        except OSError as e:return status(e)
    def remove(self,path):
        try:resolve(path).unlink();return paramiko.SFTP_OK
        except OSError as e:return status(e)
    def mkdir(self,path,attr):
        try:resolve(path).mkdir();return paramiko.SFTP_OK
        except OSError as e:return status(e)
    def rename(self,old,new):
        try:
            if resolve(new).exists():return paramiko.SFTP_FAILURE
            resolve(old).rename(resolve(new));return paramiko.SFTP_OK
        except OSError as e:return status(e)
    def rmdir(self,path):
        try:resolve(path).rmdir();return paramiko.SFTP_OK
        except OSError as e:return status(e)

def shell(channel):
    try:
        channel.send(b'\r\nLocal SSH test fixture\r\nfixture@equiplink:~$ ')
        while True:
            data=channel.recv(32768)
            if not data:break
            log('shell-input:'+data.hex());channel.send(data)
    except Exception:pass

class Server(paramiko.ServerInterface):
    def get_allowed_auths(self,username):return 'publickey,password,keyboard-interactive'
    def check_auth_none(self,username):log('auth-none');return paramiko.AUTH_FAILED
    def check_auth_password(self,username,password):
        ok=username=='fixture' and password=='local-sftp-fixture-only';log('auth-password:'+str(ok))
        return paramiko.AUTH_SUCCESSFUL if ok else paramiko.AUTH_FAILED
    def check_auth_publickey(self,username,presented):
        ok=username=='fixture' and presented==userkey;log('auth-key:'+str(ok));return paramiko.AUTH_SUCCESSFUL if ok else paramiko.AUTH_FAILED
    def check_channel_request(self,kind,chanid):return paramiko.OPEN_SUCCEEDED if kind=='session' else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED
    def check_channel_subsystem_request(self,channel,name):
        log('subsystem:'+name)
        if (base/'disable-sftp').exists():return False
        return super().check_channel_subsystem_request(channel,name)
    def check_channel_pty_request(self,*args):return True
    def check_channel_shell_request(self,channel):
        log('shell-request');threading.Thread(target=shell,args=(channel,),daemon=True).start();return True
    def check_channel_exec_request(self,channel,command):log('exec-request');return False

def serve(sock):
    transport=paramiko.Transport(sock)
    try:
        transport.add_server_key(alternate if (base/'rotate-host-key').exists() else key)
        transport.set_subsystem_handler('sftp',paramiko.SFTPServer,Files)
        transport.start_server(server=Server())
        channels=[]
        while transport.is_active():
            channel=transport.accept(0.2)
            if channel is not None: channels.append(channel)
    except Exception as e:log('transport-ended:'+type(e).__name__)
    finally:transport.close()

listener=socket.socket();listener.bind(('127.0.0.1',0));listener.listen(12)
numbers=key.key.public_key().public_numbers()
metadata={'port':listener.getsockname()[1],'puttyKey':hex(numbers.e)+','+hex(numbers.n)}
(base/'server.json').write_text(json.dumps(metadata),encoding='utf-8')
print(json.dumps(metadata),flush=True)
while True:
    sock,_=listener.accept();threading.Thread(target=serve,args=(sock,),daemon=True).start()

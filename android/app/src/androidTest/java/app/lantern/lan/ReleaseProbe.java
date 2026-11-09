package app.lantern.lan;
import android.app.*;
import android.content.*;
import android.os.*;
import android.content.pm.*;
import java.net.*;
import java.security.*;
import java.security.cert.*;
import javax.net.ssl.*;
import java.io.*;
/** Framework-only probe so the exact shrunk release can be checked without AndroidX test runtime classes. */
public class ReleaseProbe extends Instrumentation {
 private Bundle args;
 public void onCreate(Bundle value){args=value;start();}
 private void require(boolean ok,String message){if(!ok)throw new AssertionError(message);}
 public void onStart(){
  Bundle result=new Bundle();Activity camera=null;
  try{
   Context context=getTargetContext();
   require((context.getApplicationInfo().flags&ApplicationInfo.FLAG_DEBUGGABLE)==0,"Release must not be debuggable");
   PackageInfo info=context.getPackageManager().getPackageInfo(context.getPackageName(),PackageManager.GET_SIGNING_CERTIFICATES);
   require("0.7.0".equals(info.versionName)&&info.getLongVersionCode()==8,"Release version mismatch");
   boolean original=false;
   for(android.content.pm.Signature signature:info.signingInfo.getSigningCertificateHistory()){
    byte[] bytes=MessageDigest.getInstance("SHA-256").digest(signature.toByteArray());StringBuilder hex=new StringBuilder();for(byte b:bytes)hex.append(String.format("%02x",b&255));
    if(hex.toString().equals("1a78378acfdc58ae6c68f0ccdb24f9e9163933ae4d31aa071416ec582670e201"))original=true;
   }
   require(original,"Original signing identity missing");
   if(args!=null&&args.containsKey("expectedUser"))require(args.getString("expectedUser").equals(context.getSharedPreferences("settings",0).getString("user",null)),"Identity changed on upgrade");
   try(android.database.sqlite.SQLiteDatabase database=android.database.sqlite.SQLiteDatabase.openDatabase(context.getDatabasePath("history.db").getPath(),null,android.database.sqlite.SQLiteDatabase.OPEN_READONLY)){
    require(database.getVersion()==6,"History schema mismatch");
    try(android.database.Cursor cursor=database.rawQuery("SELECT COUNT(*) FROM messages",null)){cursor.moveToFirst();if(args!=null&&args.containsKey("expectedMessages"))require(cursor.getLong(0)==Long.parseLong(args.getString("expectedMessages")),"History changed on upgrade");}
   }
   camera=startActivitySync(new Intent().setClassName(context,"app.lantern.lan.LiveCameraActivity").putExtra("peerIp","127.0.0.1").putExtra("peerUser","browser-test").addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
   java.lang.reflect.Field serverField=camera.getClass().getDeclaredField("server");serverField.setAccessible(true);Object server=null;long end=System.currentTimeMillis()+45000;
   while(System.currentTimeMillis()<end){server=serverField.get(camera);if(server!=null&&server.getClass().getMethod("getFrame").invoke(server)!=null)break;Thread.sleep(100);}
   require(server!=null && server.getClass().getMethod("getFrame").invoke(server)!=null,"Release camera did not publish a frame");
   String url=(String)server.getClass().getMethod("getUrl").invoke(server);URI address=new URI(url);
   Class<?> helper=Class.forName("app.lantern.lan.CameraTls");Object identity=helper.getMethod("load",Context.class,String.class).invoke(helper.getField("INSTANCE").get(null),context,"127.0.0.1");
   X509Certificate certificate=(X509Certificate)identity.getClass().getMethod("getCertificate").invoke(identity);
   KeyStore trust=KeyStore.getInstance(KeyStore.getDefaultType());trust.load(null);trust.setCertificateEntry("camera",certificate);
   TrustManagerFactory managers=TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm());managers.init(trust);
   SSLContext ssl=SSLContext.getInstance("TLS");ssl.init(null,managers.getTrustManagers(),null);
   try(SSLSocket socket=(SSLSocket)ssl.getSocketFactory().createSocket(address.getHost(),address.getPort())){
    socket.setSoTimeout(10000);SSLParameters parameters=socket.getSSLParameters();parameters.setEndpointIdentificationAlgorithm("HTTPS");socket.setSSLParameters(parameters);socket.startHandshake();
    socket.getOutputStream().write(("GET / HTTP/1.1\r\nHost: "+address.getHost()+":"+address.getPort()+"\r\n\r\n").getBytes(java.nio.charset.StandardCharsets.UTF_8));
    ByteArrayOutputStream body=new ByteArrayOutputStream();byte[] buffer=new byte[8192];int count;while((count=socket.getInputStream().read(buffer))!=-1)body.write(buffer,0,count);
    require(body.toString("UTF-8").contains("Secure live camera"),"Release HTTPS viewer missing");
   }
   String origin="https://"+address.getHost()+":"+address.getPort();String auth;
   try(SSLSocket socket=verifiedSocket(ssl,address)){
    socket.getOutputStream().write(("POST /auth HTTP/1.1\r\nHost: "+address.getHost()+":"+address.getPort()+"\r\nOrigin: "+origin+"\r\nX-IPMsg-Token: "+address.getFragment().substring(4)+"\r\nContent-Length: 0\r\n\r\n").getBytes(java.nio.charset.StandardCharsets.US_ASCII));
    ByteArrayOutputStream data=new ByteArrayOutputStream();byte[] chunk=new byte[4096];int n;while((n=socket.getInputStream().read(chunk))!=-1)data.write(chunk,0,n);auth=data.toString("UTF-8");
   }
   require(auth.contains("200"),"Release authentication failed");String cookie=auth.split("Set-Cookie: ")[1].split(";")[0];
   try(SSLSocket socket=verifiedSocket(ssl,address)){
    socket.getOutputStream().write(("GET /stream HTTP/1.1\r\nHost: "+address.getHost()+":"+address.getPort()+"\r\nCookie: "+cookie+"\r\n\r\n").getBytes(java.nio.charset.StandardCharsets.US_ASCII));
    InputStream input=new BufferedInputStream(socket.getInputStream());String line;boolean multipart=false;
    while(!(line=readLine(input)).isEmpty())if(line.contains("multipart/x-mixed-replace"))multipart=true;
    require(multipart,"Release stream is not multipart JPEG");require(readLine(input).equals("--ipmsgframe"),"Missing frame boundary");int length=0;
    while(!(line=readLine(input)).isEmpty())if(line.startsWith("Content-Length:"))length=Integer.parseInt(line.substring(15).trim());
    require(length>100 && length<2000000,"Invalid frame size");byte[] image=new byte[length];int offset=0;while(offset<length){int n=input.read(image,offset,length-offset);require(n>0,"Incomplete stream frame");offset+=n;}
    android.graphics.Bitmap frame=android.graphics.BitmapFactory.decodeByteArray(image,0,image.length);require(frame!=null&&frame.getWidth()>0&&frame.getHeight()>0,"Release stream JPEG did not decode");frame.recycle();
   }
   result.putString("result","PASS: non-debuggable release, signing lineage, preserved identity/history and authenticated HTTPS camera stream");finish(0,result);
  }catch(Throwable failure){result.putString("result","FAIL: "+failure);finish(1,result);}
  finally{if(camera!=null){Activity owner=camera;runOnMainSync(owner::finish);}}
 }
 private SSLSocket verifiedSocket(SSLContext context,URI address)throws Exception{
  SSLSocket socket=(SSLSocket)context.getSocketFactory().createSocket(address.getHost(),address.getPort());socket.setSoTimeout(10000);SSLParameters parameters=socket.getSSLParameters();parameters.setEndpointIdentificationAlgorithm("HTTPS");socket.setSSLParameters(parameters);socket.startHandshake();return socket;
 }
 private String readLine(InputStream input)throws IOException{
  ByteArrayOutputStream bytes=new ByteArrayOutputStream();while(bytes.size()<8192){int value=input.read();if(value<0)throw new EOFException();if(value==10)return bytes.toString("US-ASCII").replace("\r","");bytes.write(value);}throw new IOException("Oversized HTTP line");
 }

}

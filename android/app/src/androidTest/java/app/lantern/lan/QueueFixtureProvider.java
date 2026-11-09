package app.lantern.lan;
import android.content.*;
import android.database.*;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;
import java.io.*;
/** Read-only external provider; test APK only. No target-app runtime dependency. */
public class QueueFixtureProvider extends ContentProvider {
 public boolean onCreate(){return true;}
 public String getType(Uri uri){return "text/plain";}
 public Cursor query(Uri uri,String[] projection,String selection,String[] args,String sort){
  String[] columns=projection!=null?projection:new String[]{OpenableColumns.DISPLAY_NAME,OpenableColumns.SIZE};
  Object[] row=new Object[columns.length];
  for(int i=0;i<columns.length;i++)row[i]=OpenableColumns.DISPLAY_NAME.equals(columns[i])?"queue-sample.txt":Long.valueOf(3);
  MatrixCursor cursor=new MatrixCursor(columns);cursor.addRow(row);return cursor;
 }
 public ParcelFileDescriptor openFile(Uri uri,String mode)throws FileNotFoundException{
  if(!"r".equals(mode))throw new FileNotFoundException("Read only");
  try{
   ParcelFileDescriptor[] pipe=ParcelFileDescriptor.createPipe();
   new Thread(()->{try(OutputStream out=new ParcelFileDescriptor.AutoCloseOutputStream(pipe[1])){out.write(new byte[]{1,2,3});}catch(IOException ignored){}}).start();
   return pipe[0];
  }catch(IOException e){throw new FileNotFoundException(e.getMessage());}
 }
 public Uri insert(Uri uri,ContentValues values){return null;}
 public int update(Uri uri,ContentValues values,String selection,String[] args){return 0;}
 public int delete(Uri uri,String selection,String[] args){return 0;}
}

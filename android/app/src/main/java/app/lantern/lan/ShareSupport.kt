package app.lantern.lan

import android.content.Intent
import android.net.Uri
import android.provider.OpenableColumns
import androidx.core.content.FileProvider
import java.io.File

object ShareSupport {
    fun metadata(model: Model, uri: Uri): Pair<String,Long> = model.context.contentResolver.query(uri,arrayOf(OpenableColumns.DISPLAY_NAME,OpenableColumns.SIZE),null,null,null)?.use { c ->
        require(c.moveToFirst() && !c.isNull(1)) { "This provider does not report a file size. Save it locally first." }
        Wire.safeName(c.getString(0) ?: "file") to c.getLong(1).also { require(it >= 0) }
    } ?: error("Cannot read this file")
    @Suppress("DEPRECATION") fun received(intent: Intent): List<Uri> {
        val uris = when(intent.action) {
            Intent.ACTION_SEND -> listOfNotNull(intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM))
            Intent.ACTION_SEND_MULTIPLE -> intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM).orEmpty()
            else -> emptyList()
        }
        require(uris.size <= 30) { "Share at most 30 files at a time" }
        require(uris.all { it.scheme == "content" }) { "Only granted content files can be shared" }
        return uris.distinct()
    }
    fun durable(model: Model, uri: Uri): Uri {
        if(uri.authority == model.context.packageName+".files") return uri
        if(uri.authority == android.provider.MediaStore.AUTHORITY && runCatching {model.context.contentResolver.query(uri,arrayOf(android.provider.MediaStore.MediaColumns.OWNER_PACKAGE_NAME),null,null,null)?.use{it.moveToFirst() && it.getString(0)==model.context.packageName}==true}.getOrDefault(false)) return uri
        runCatching { model.context.contentResolver.takePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION) }
        if(model.context.contentResolver.persistedUriPermissions.any { it.uri == uri && it.isReadPermission }) return uri
        val (name,size) = metadata(model,uri)
        require(android.os.StatFs(model.context.filesDir.absolutePath).availableBytes > size + 8L*1024*1024) { "Not enough space to keep this queued file" }
        val directory = File(model.context.filesDir,"outbox/${java.util.UUID.randomUUID()}").apply { mkdirs() }
        val file = File(directory,name)
        try { model.context.contentResolver.openInputStream(uri)!!.use { input -> file.outputStream().use { output -> input.copyTo(output,131072) } }; require(file.length() == size) { "Source changed while preparing the queue" } }
        catch(e: Exception) { file.delete(); throw e }
        return FileProvider.getUriForFile(model.context,"${model.context.packageName}.files",file)
    }
    fun cleanup(model: Model) {
        val keep=model.repository.outbox().flatMap{it.uris}.toSet()
        File(model.context.filesDir,"outbox").takeIf{it.exists()}?.walkBottomUp()?.forEach {file ->
            if(file.isFile && System.currentTimeMillis()-file.lastModified()>48L*3_600_000 && FileProvider.getUriForFile(model.context,model.context.packageName+".files",file).toString() !in keep) file.delete()
            else if(file.isDirectory && file.listFiles()?.isEmpty()==true) file.delete()
        }
    }
}

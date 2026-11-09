package app.lantern.lan

enum class CameraQuality(val title:String,val width:Int,val height:Int,val intervalMs:Long,val jpegQuality:Int){
    SMOOTH("Smooth",640,480,83,50), CLEAR("Clear",1280,720,125,72);
    companion object {fun from(value:String?)=entries.firstOrNull{it.name==value} ?: SMOOTH}
}

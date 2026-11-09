package app.lantern.lan

import java.nio.ByteBuffer

/** Stride-aware YUV420 conversion; no bitmap readback and no frame queue. */
object CameraFrames {
    data class Plane(val bytes: ByteBuffer,val rowStride: Int,val pixelStride: Int)
    class Workspace {
        private val planes=arrayOf(ByteArray(0),ByteArray(0),ByteArray(0))
        fun read(index:Int,plane:Plane):ByteArray {
            val source=plane.bytes.duplicate()
            if(planes[index].size<source.remaining())planes[index]=ByteArray(source.remaining())
            source.get(planes[index],0,source.remaining())
            return planes[index]
        }
    }
    fun pack(width: Int,height: Int,y: Plane,u: Plane,v: Plane,rotation: Int,out: ByteArray,workspace:Workspace=Workspace()): ByteArray {
        require(width>0 && height>0 && width%2==0 && height%2==0 && rotation in listOf(0,90,180,270))
        require(out.size==width*height*3/2)
        // Camera planes are direct buffers. Bulk reads avoid a native buffer call for every pixel.
        val ys=workspace.read(0,y);val us=workspace.read(1,u);val vs=workspace.read(2,v)
        fun start(row:Int,w:Int,h:Int)=when(rotation){90->h-1-row;180->(h-1-row)*w+w-1;270->(w-1)*h+row;else->row*w}
        fun step(h:Int)=when(rotation){90->h;180->-1;270->-h;else->1}
        val yStep=step(height)
        for(row in 0 until height){var target=start(row,width,height);var source=row*y.rowStride
            for(x in 0 until width){out[target]=ys[source];target+=yStep;source+=y.pixelStride}}
        val offset=width*height;val cw=width/2;val ch=height/2
        val cStep=2*step(ch)
        for(row in 0 until ch){var target=offset+2*start(row,cw,ch);var ui=row*u.rowStride;var vi=row*v.rowStride
            for(x in 0 until cw){out[target]=vs[vi];out[target+1]=us[ui];target+=cStep;ui+=u.pixelStride;vi+=v.pixelStride}}
        return out
    }
    /** Downsample and orient directly into the output buffer; low quality avoids full-resolution pixel loops. */
    fun packScaled(width:Int,height:Int,y:Plane,u:Plane,v:Plane,rotation:Int,percent:Int,out:ByteArray,workspace:Workspace):Pair<Int,Int> {
        val sw=((width*percent/100) and -2).coerceAtLeast(2)
        val sh=((height*percent/100) and -2).coerceAtLeast(2)
        require(out.size==sw*sh*3/2)
        val ys=workspace.read(0,y);val us=workspace.read(1,u);val vs=workspace.read(2,v)
        fun start(row:Int,w:Int,h:Int)=when(rotation){90->h-1-row;180->(h-1-row)*w+w-1;270->(w-1)*h+row;else->row*w}
        fun step(h:Int)=when(rotation){90->h;180->-1;270->-h;else->1}
        val stride=step(sh)
        for(row in 0 until sh){var target=start(row,sw,sh);val sourceRow=(row*height/sh)*y.rowStride
            for(x in 0 until sw){out[target]=ys[sourceRow+(x*width/sw)*y.pixelStride];target+=stride}}
        val cw=sw/2;val ch=sh/2;val chromaStride=2*step(ch)
        for(row in 0 until ch){var target=sw*sh+2*start(row,cw,ch);val sourceRow=row*(height/2)/ch
            for(x in 0 until cw){val col=x*(width/2)/cw;out[target]=vs[sourceRow*v.rowStride+col*v.pixelStride];out[target+1]=us[sourceRow*u.rowStride+col*u.pixelStride];target+=chromaStride}}
        return if(rotation%180==0)sw to sh else sh to sw
    }

}

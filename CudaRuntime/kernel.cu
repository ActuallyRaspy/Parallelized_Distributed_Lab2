// kernel.cu — bygg som Dynamic Library (.dll), x64
#include <cuda_runtime.h>
#include <device_launch_parameters.h>
#include <stdint.h>

// ======================================================================
//  Device-funktion: mandelIter
//  - Kör själva Mandelbrot-iterationen z_{n+1} = z_n^2 + c för EN pixel.
//  - Returnerar antal iterationer (0..maxIter).
//  - __device__ = körs på GPU, __forceinline__ = be NVCC inlinea funktionen.
//  OBS: Måste finnas före kernel (eller ha en funktionsprototyp).
// ======================================================================
__device__ __forceinline__ int mandelIter(double cx, double cy, int maxIter) {
    double x = 0.0, y = 0.0;
    int it = 0;
    while ((x * x + y * y) <= 4.0 && it < maxIter) {
        double xt = x * x - y * y + cx;
        y = 2.0 * x * y + cy;
        x = xt;
        ++it;
    }
    return it;
}

// ======================================================================
//  Kernel: MandelKernel1D
//  - EN global trådindex (1D) används och mappas till (ix, iy) i bilden.
//  - Beräknar koordinat (cx,cy), kör mandelIter(), och skriver ut BGRA32.
// ======================================================================
__global__ void MandelKernel1D(uint32_t* outBGRA, int width, int height,
    double xmin, double xmax, double ymin, double ymax,
    int maxIter)
{
    // Globalt 1D-index för tråden
    size_t idx = blockIdx.x * blockDim.x + threadIdx.x;
    size_t n = (size_t)width * (size_t)height;
    if (idx >= n) return;   // skydda mot out-of-bounds

    // Mappa 1D-index -> (ix, iy)
    int ix = (int)(idx % (size_t)width);
    int iy = (int)(idx / (size_t)width);

    // Linjär mapping från pixelkoordinat till komplex plan (cx, cy)
    double cx = xmin + (xmax - xmin) * (double)ix / (double)(width - 1);
    double cy = ymin + (ymax - ymin) * (double)iy / (double)(height - 1);

    // Iterera Mandelbrot och normalisera till 0..255
    int it = mandelIter(cx, cy, maxIter);
    unsigned char v = (unsigned char)(255.0 * (double)it / (double)maxIter);

    // Skriv en BGRA32-pixel (0xAARRGGBB). Här: gråskala + full alfa (AA=0xFF).
    // (Little-endian i minnet: BB GG RR AA.)
    uint32_t pixel = (0xFFu << 24) | (v << 16) | (v << 8) | v; // gråskala + full alfa
    outBGRA[idx] = pixel;
}

// ======================================================================
//  Exporterade funktioner (C-ABI) som C# DllImport anropar
//  - Håll extern "C" endast för API:t som ska synas utåt.
//  - setCudaDevice: välj GPU (0 = default).
// ======================================================================
extern "C" __declspec(dllexport) int __cdecl setCudaDevice(int device)
{
    return (int)cudaSetDevice(device);    // Returnerar CUDA-felkod (0 = cudaSuccess)
}

// ======================================================================
//  update-funktion kallad från C#:
//  - Allokerar device-minne (dOut)
//  - Sätter grid/block (1D-konfiguration)
//  - Startar MandelKernel1D
//  - Synkroniserar och kopierar tillbaka till host-bufferten outBGRA (BGRA32)
//  - Frigör device-minne
//  Returnerar CUDA-felkod (0 = OK).
//  OBS: outBGRA är en pekare till en redan allokerad hostbuffer i C# (byte[])!
// ======================================================================
extern "C" __declspec(dllexport) int __cdecl UpdateMandelCuda(
    uint32_t* outBGRA, int width, int height,
    double xmin, double xmax, double ymin, double ymax,
    int maxIter)
{
    if (!outBGRA || width <= 0 || height <= 0 || maxIter <= 0) return -1; // Grundläggande argumentkontroll

    // Antal pixlar och antal byte i output-bufferten (BGRA32)
    size_t nPix = (size_t)width * (size_t)height;
    size_t bytes = nPix * sizeof(uint32_t);

    // 1) Allokera device-output
    uint32_t* dOut = nullptr;
    cudaError_t st = cudaMalloc(&dOut, bytes);
    if (st) return (int)st; // avbryt direkt om minnet inte kan allokeras

    // 2) Välj block/grid (1D). 256 trådar/block är vanligt standardval.
    int block = 256;
    int grid = (int)((nPix + block - 1) / block);

    // 3) Starta kernel
    MandelKernel1D << <grid, block >> > (dOut, width, height, xmin, xmax, ymin, ymax, maxIter);

    // 4) Fånga launch-fel och körfel
    st = cudaGetLastError();      if (st) { cudaFree(dOut); return (int)st; }
    st = cudaDeviceSynchronize(); if (st) { cudaFree(dOut); return (int)st; }


    // 5) Kopiera tillbaka resultatet (BGRA32) till host-bufferten som C# gav oss
    st = cudaMemcpy(outBGRA, dOut, bytes, cudaMemcpyDeviceToHost);
    cudaFree(dOut);

    return (int)st;   // 0 = OK, annars CUDA-felkod (C# loggar rc om rc != 0)
}

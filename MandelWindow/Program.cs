using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Timers;

namespace MandelWindow
{
    class Program
    {
        // --------------------------------------------------------------------
        // Dll Imports
        // --------------------------------------------------------------------
        //lade till här
        // P/Invoke-deklarationer för att anropa funktioner i CudaRuntime.dll.
        // - setCudaDevice(0) väljer GPU-enhet.
      
        // - UpdateMandelCuda(...) är vår riktiga GPU-renderare: fyller en BGRA32-buffert med Mandelbrot-pixlar.


        [DllImport("CudaRuntime.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int setCudaDevice(int device);

        [DllImport("CudaRuntime.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int addWithCuda([Out] int[] c, [In] int[] a, [In] int[] b, uint size);

        [DllImport("CudaRuntime.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int UpdateMandelCuda(
        IntPtr outBGRA, int width, int height,
        double xmin, double xmax, double ymin, double ymax,
        int maxIter);
        // --------------------------------------------------------------------
        // Attributes
        // --------------------------------------------------------------------

        // For displaying an image in a window
        static WriteableBitmap bitmap;
        static Window window;
        static Image image;

        // For animating zooming in/out
        static Thread mandelThread;
        static volatile bool activeMandelThread = true;
        static double zoomCenterX = -0.16349229306767682;
        static double zoomCenterY = -1.0260970739840185;
        static int stepsCounter = 1;
        static int stepsDirection = 1;

        // The bounds for the Mandelbrot rectangle
        static double mandelCenterX = 0.0;
        static double mandelCenterY = 0.0;
        static double mandelWidth = 2.0;
        static double mandelHeight = 2.0;

        // The maximum depth when iterating a Madelbrot coordinate
        public static int mandelDepth = 360;

        // If true, the code calls UpdateMandelParallel(), else it calls UpdateMandel.
        static bool useParallel = false;

        static long lastCpuMs, lastGpuMs; // Senaste mättiderna för enstaka renderingar, så att vi kan visa/skriva ut CPU/GPU-tider.


        // --------------------------------------------------------------------
        // Main Method
        // --------------------------------------------------------------------

        /// <summary>
        /// The Main method creates a WPF application consisting of one Window displaying an Image.
        /// A WriteableBitmap is used as the Image's source, and is what is displayed in the Image.
        /// Variables are defined with Default values for the Mandelbrot rectangle
        /// (the bounds of the WriteableBitmap), and for zooming in/out of the Image.
        /// The Main method also creates and starts a Thread implementing the the zooming functionality,
        /// and sets up eventhandlers for the mouse and keyboard.
        /// </summary>
        /// <param name="args"></param>
        [STAThread]
        static void Main(string[] args)
        {
            // Create an Image
            image = new Image();
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetEdgeMode(image, EdgeMode.Aliased);

            // Create a Window and set the Image as its Content
            window = new Window();
            window.Content = image;
            window.Show();

            // Create a WriteableBitmap and set it as the Image's Source
            bitmap = new WriteableBitmap
            (
                (int)window.ActualWidth,
                (int)window.ActualHeight,
                96,
                96,
                PixelFormats.Bgr32,
                null
            );
            image.Source = bitmap;
            image.Stretch = Stretch.None;
            image.HorizontalAlignment = HorizontalAlignment.Left;
            image.VerticalAlignment = VerticalAlignment.Top;

            // Set up event handlers
            image.MouseLeftButtonDown += new MouseButtonEventHandler(image_MouseLeftButtonDown);
            image.MouseRightButtonDown += new MouseButtonEventHandler(image_MouseRightButtonDown);
            image.MouseMove += new MouseEventHandler(image_MouseMove);
            window.MouseWheel += new MouseWheelEventHandler(window_MouseWheel);
            window.Closing += Windows_Closing;
            window.KeyDown += Windows_KeyDown;

            // Create the WPF Application
            Application app = new Application();

            // Update the WriteableBitmap
            if (useParallel) UpdateMandelParallel();
            else UpdateMandel();

            // This thread is used to zoom in/out of the Image
            mandelThread = new Thread(() =>
            {
                while (activeMandelThread)
                {
                    if (stepsCounter > 0)
                    {
                        stepsCounter--;
                        if (stepsDirection == 1)
                        {
                            mandelCenterX = (mandelCenterX * 0.8 + zoomCenterX * 0.2);
                            mandelCenterY = (mandelCenterY * 0.8 + zoomCenterY * 0.2);
                            mandelWidth *= 0.9;
                            mandelHeight *= 0.9;
                            mandelDepth += 2;
                        }
                        else if (stepsDirection == -1)
                        {
                            mandelCenterX = (mandelCenterX * 0.8 + zoomCenterX * 0.2);
                            mandelCenterY = (mandelCenterY * 0.8 + zoomCenterY * 0.2);
                            mandelWidth *= 1.1;
                            mandelHeight *= 1.1;
                            mandelDepth -= 2;
                        }
                        if (useParallel) UpdateMandelParallel();
                        else UpdateMandel();
                    }
                    Thread.Sleep(1);
                }
            });

            // Start the Thread and run the WPF Application
            mandelThread.Start();
            app.Run();
        }

        // --------------------------------------------------------------------
        // Event Handlers
        // --------------------------------------------------------------------

        /// <summary>
        /// This event handler calls the C-functions setCudaDevice() and
        /// addWithCuda() in the CudaRuntime.dll when the "V" key is pressed.
        /// It provides a simple example using .NET Interop to pass three
        /// arrays (a, b, and c) to the C-function, which calculates c = a + b.
        /// After the call, the values in the three arrays are printed out.
        /// It also shows how to use the "Stopwatch" class to time the execution,
        /// and the Trace class to print output to standard output in a WPF app.
        /// Finally, it toggles between updating the Mandelbrot fractal on the
        /// GPU and CPU if the "T" key is pressed.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private static void Windows_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.V)
            {
                const int arraySize = 5;
                int[] a = { 1, 2, 3, 4, 5 };
                int[] b = { 10, 20, 30, 40, 50 };
                int[] c = { 0, 0, 0, 0, 0 };

                // Call the C-function setCudaDevice()
                setCudaDevice(0);

                Stopwatch sw = Stopwatch.StartNew();
                // Call the C-function addWithCuda()
                int result = addWithCuda(c, a, b, arraySize);
                sw.Stop();

                Trace.WriteLine($"GPU vector addition took {sw.ElapsedMilliseconds} ms");
                for (int i = 0; i < arraySize; ++i)
                    Trace.WriteLine($"{a[i]} + {b[i]} = {c[i]}");
            }
            else if (e.Key == Key.T)
            {
                useParallel = !useParallel;
                window.Title = $"Mode: {(useParallel ? "GPU (parallel)" : "CPU (original)")}";
                if (useParallel) UpdateMandelParallel();
                else UpdateMandel();
            }
            // NYTT (Lab 2): Mätningar och CSV-export via F5/F6/E
            else if (e.Key == Key.F5) TimeCpuOnce();  // F5: Mät en enda CPU-render (uppdaterar lastCpuMs och loggar ms).
            else if (e.Key == Key.F6) TimeGpuOnce();  // F6: Mät en enda GPU-render (uppdaterar lastGpuMs och loggar ms).
            else if (e.Key == Key.E)  // E: Kör ett litet experiment där vi zoomar N steg mot en vald punkt
                                      // och mäter total tid för CPU respektive GPU, räknar speedup och skriver CSV.
            {
                int steps = 60;                  // Antal zoom-steg i experimentet (valfritt).
                double tx = -0.75, ty = 0.1;     // Målkoordinat i Mandelbrotplanet (valfri punkt).

                // Kör zoom-experimentet på CPU och GPU (se RunZoomExperiment).
                var cpu = RunZoomExperiment(gpu: false, steps: steps, targetX: tx, targetY: ty);
                var gpu = RunZoomExperiment(gpu: true, steps: steps, targetX: tx, targetY: ty);

                double speedup = (cpu.totalMs > 0) ? (double)cpu.totalMs / (double)gpu.totalMs : 0.0; // Speedup = CPU-tid / GPU-tid (större är bättre; >1 betyder att GPU är snabbare).

                // Bygg CSV-innehåll. Första raden är header.
                // Kolumner: mode,total_ms,steps,width,height,maxIter,timestamp
                string csv =
                    "mode,total_ms,steps,width,height,maxIter,timestamp\n" +
                    $"CPU,{cpu.totalMs},{cpu.steps},{bitmap.PixelWidth},{bitmap.PixelHeight},{mandelDepth},{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" + // Mätresultat för CPU (total ms över alla 'steps').
                    $"GPU,{gpu.totalMs},{gpu.steps},{bitmap.PixelWidth},{bitmap.PixelHeight},{mandelDepth},{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" + // Mätresultat för GPU (total ms över alla 'steps').
                    $"Speedup(x),{speedup:F2},,,,\n"; // Extra rad med speedup (praktiskt att plotta i Excel).

                // Spara CSV till skrivbordet som "mandelbrot_timing.csv".
                string path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "mandelbrot_timing.csv");
                System.IO.File.WriteAllText(path, csv, Encoding.UTF8);

                // Logga var filen hamnade och visa resultat i fönstertiteln.
                Debug.WriteLine($"Saved CSV: {path}");
                window.Title = $"CPU {cpu.totalMs} ms | GPU {gpu.totalMs} ms | Speedup {speedup:F2}x";
            }


        }

        /// <summary>
        /// This event handler stops the Thread and waits for it to terminate
        /// when the Window closes.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private static void Windows_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            activeMandelThread = false;
            mandelThread.Join();
        }

        /// <summary>
        /// When the right mouse buttom is clicked on the Image in the Window,
        /// the pixel coordinate is converted to a Mandelbrot coordinate in the
        /// complex plane, which is used to set a zoom "target" for animating
        /// zooming out of the Image.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        static void image_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Get pixel coordinate
            int column = (int)e.GetPosition(image).X;
            int row = (int)e.GetPosition(image).Y;

            // Convert Image (pixel) coordinates to Mandelbrot rectangle coordinates (in the complex plane)
            zoomCenterX = mandelCenterX - mandelWidth + column * ((mandelWidth * 2.0) / bitmap.PixelWidth);
            zoomCenterY = mandelCenterY - mandelHeight + row * ((mandelHeight * 2.0) / bitmap.PixelHeight);

            // Set zoom direction and number of zoom steps
            stepsDirection = -1;
            stepsCounter = 10;
        }

        /// <summary>
        /// When the left mouse buttom is clicked on the Image in the Window,
        /// the pixel coordinate is converted to a Mandelbrot coordinate in the
        /// complex plane, which is used to set a zoom "target" for animating
        /// zooming in to the Image.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        static void image_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Get pixel coordinate
            int column = (int)e.GetPosition(image).X;
            int row = (int)e.GetPosition(image).Y;

            // Convert Image (pixel) coordinates to Mandelbrot rectangle coordinates (in the complex plane)
            zoomCenterX = mandelCenterX - mandelWidth + column * ((mandelWidth * 2.0) / bitmap.PixelWidth);
            zoomCenterY = mandelCenterY - mandelHeight + row * ((mandelHeight * 2.0) / bitmap.PixelHeight);

            // Set zoom direction and number of zoom steps
            stepsDirection = 1;
            stepsCounter = 10;
        }

        /// <summary>
        /// When the mouse wheel is used in the Window, the mouse cursor's
        /// pixel coordinate is converted to a Mandelbrot coordinate in the
        /// complex plane, which is used to immediately zoom (without animation)
        /// the image in/out depending on the rotation direction of the wouse wheel.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        static void window_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Get pixel coordinate
            int column = (int)e.GetPosition(image).X;
            int row = (int)e.GetPosition(image).Y;

            // Convert Image (pixel) coordinates to Mandelbrot rectangle coordinates (in the complex plane)
            mandelCenterX = mandelCenterX - mandelWidth + column * ((mandelWidth * 2.0) / bitmap.PixelWidth);
            mandelCenterY = mandelCenterY - mandelHeight + row * ((mandelHeight * 2.0) / bitmap.PixelHeight);

            // Get mouse wheel rotation direction
            if (e.Delta > 0)
            {
                // Zoom in
                mandelWidth /= 2.0;
                mandelHeight /= 2.0;
            }
            else
            {
                // Zoom out
                mandelWidth *= 2.0;
                mandelHeight *= 2.0;
            }

            // Update the WriteableBitmap for the new Mandelbrot rectangle boundaries
            if (useParallel) UpdateMandelParallel();
            else UpdateMandel();
        }

        /// <summary>
        /// When the mouse cursor is moved over the Image in the Window,
        /// the pixel coordinate is converted to a Mandelbrot coordinate in the
        /// complex plane, then the coordinate is displayed in the Window's title.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        static void image_MouseMove(object sender, MouseEventArgs e)
        {
            // Get pixel coordinate
            int column = (int)e.GetPosition(image).X;
            int row = (int)e.GetPosition(image).Y;

            // Convert Image (pixel) coordinates to Mandelbrot rectangle coordinates (in the complex plane)
            double mouseCenterX = mandelCenterX - mandelWidth + column * ((mandelWidth * 2.0) / bitmap.PixelWidth);
            double mouseCenterY = mandelCenterY - mandelHeight + row * ((mandelHeight * 2.0) / bitmap.PixelHeight);

            // Display the coordinate in the Window's title
            window.Title = $"Mandelbrot coordinates X:{mouseCenterX} Y:{mouseCenterY}";
        }

        // --------------------------------------------------------------------
        // Methods
        // --------------------------------------------------------------------

        /// <summary>
        /// This method updates the WriteableBitmap, displayed in the Image.
        /// Firstly, it does a context switch to the primary (UI) thread.
        /// Then it loops through each pixel in the WriteableBitmap,
        /// converts the pixel coordinate to a Mandelbrot coordinate,
        /// uses the Mandelbrot coordinate to calculate the number of
        /// iterations required using the "Escape-Time Algorithm",
        /// converts the iteration count (considered a "Hue") from
        /// HSV to RGB, and finally assignes the RGB color back to the
        /// pixel in the WriteableBitmap.
        /// </summary>
        public static void UpdateMandel()
        {
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                try
                {
                    // Reserve the back buffer for updates.
                    bitmap.Lock();

                    // Use unsafe block since we are using pointer syntax "*((int*)pBackBuffer) = color_data;"
                    unsafe
                    {
                        // Loop through all pixels in the WriteableBitmap
                        for (int row = 0; row < bitmap.PixelHeight; row++)
                        {
                            for (int column = 0; column < bitmap.PixelWidth; column++)
                            {
                                // Get a pointer to the back buffer, where IntPtr represents a "raw" pointer.
                                // It contains the address to the first byte in the pixel array.
                                IntPtr pBackBuffer = bitmap.BackBuffer;

                                // Find the address of the pixel to draw.
                                // Uses pointer arithmetic to calculate the
                                // address of the current pixel in the pixel array.
                                pBackBuffer += row * bitmap.BackBufferStride; // BackBufferStride = number of bytes in one row
                                pBackBuffer += column * 4;                   // "4" means 4 bytes (size of a "pixel"/"uint")

                                // Convert Image (pixel) coordinates to Mandelbrot rectangle coordinates (in the complex plane).
                                double cx = mandelCenterX - mandelWidth + column * ((mandelWidth * 2.0) / bitmap.PixelWidth);
                                double cy = mandelCenterY - mandelHeight + row * ((mandelHeight * 2.0) / bitmap.PixelHeight);

                                // Get the number of iterations "light" for this coordinate using the "Escape-Time Algorithm".
                                int light = IterCount(cx, cy);

                                // Convert the "light" (considered a "Hue") from HSV to RGB.
                                int R, G, B;
                                HsvToRgb(light, 1.0, light < mandelDepth ? 1.0 : 0.0, out R, out G, out B);

                                // Compute the pixel's color (uses bit-shifts to produce 0xRRGGBB).
                                int color_data = R << 16; // R
                                color_data |= G << 8; // G
                                color_data |= B << 0; // B

                                // Assign the color data to the pixel.
                                *((int*)pBackBuffer) = color_data;
                            }
                        }
                    }

                    // Specify the area of the bitmap that changed.
                    bitmap.AddDirtyRect(new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
                }
                finally
                {
                    // Release the back buffer and make it available for display.
                    bitmap.Unlock();
                }
            }));
        }

        /// <summary>
        /// TODO: Implement updating the Mandelbrot fractal on the GPU.
        /// </summary>
        // NYTT (Lab 2) – GPU-rendering med CUDA
        // 1) setCudaDevice(0) säkerställer att rätt GPU väljs (kan göras en gång vid start).
        // 2) Beräkna aktuella Mandelbrot-gränser (xmin/xmax/ymin/ymax) och maxIter.
        // 3) Allokera en managed byte[]-buffer i BGRA32 (stride*h) för hela bilden.
        // 4) Anropa UpdateMandelCuda(...) som fyller bufferten på GPU:n (en tråd per pixel).
        // 5) Skriv tillbaka bufferten till WPF-bitmapen med WritePixels.
        // Obs: Vi använder WritePixels istället för att skriva direkt i BackBuffer eftersom CUDA fyller
        //      vår egen buffer. Formatet är BGRA32, 8 bitar per kanal.

        public static void UpdateMandelParallel()
        {
            setCudaDevice(0); // kan göras en gång vid start också. Kan flyttas till programstart (t.ex. i Main) om du vill undvika att kalla den varje gång.

            ///UpdateMandel();
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                try
                {
                    


                    int w = bitmap.PixelWidth;
                    int h = bitmap.PixelHeight;
                    int stride = bitmap.BackBufferStride;   // säkrare om stride ≠ w*4
                    int bytes = stride * h;

                    double xmin = mandelCenterX - mandelWidth;
                    double xmax = mandelCenterX + mandelWidth;
                    double ymin = mandelCenterY - mandelHeight;
                    double ymax = mandelCenterY + mandelHeight;
                    int maxIter = mandelDepth;

                    byte[] pixels = new byte[bytes];


                    bitmap.Lock();
                    try
                    {
                        unsafe
                        {
                            fixed (byte* p = pixels)
                            {   // Här sker övergången till nativ CUDA-kod: device-funktionen itererar z^2+c per pixel och
                                // returnerar en 32-bitars BGRA-färg. rc != 0 betyder CUDA-felkod (loggas till Debug).
                                int rc = UpdateMandelCuda((IntPtr)p, w, h, xmin, xmax, ymin, ymax, maxIter);
                                if (rc != 0) Debug.WriteLine($"GPU error (cuda code) = {rc}");
                            }
                        }
                        bitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
                    }
                    finally
                    {
                        bitmap.AddDirtyRect(new Int32Rect(0, 0, w, h));
                        bitmap.Unlock();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.ToString());
                }
            }));
        }

        // Mäter en enda CPU-render (UpdateMandel) med Stopwatch och loggar resultatet.
        static long TimeCpuOnce()
        {
            var sw = Stopwatch.StartNew();
            UpdateMandel();
            sw.Stop();
            lastCpuMs = sw.ElapsedMilliseconds;
            Debug.WriteLine($"CPU render: {lastCpuMs} ms");
            return lastCpuMs;
        }

        // Mäter en enda GPU-render (UpdateMandelParallel) med Stopwatch och loggar resultatet.
        static long TimeGpuOnce()
        {
            var sw = Stopwatch.StartNew();
            UpdateMandelParallel();
            sw.Stop();
            lastGpuMs = sw.ElapsedMilliseconds;
            Debug.WriteLine($"GPU render: {lastGpuMs} ms");
            return lastGpuMs;
        }

        // Kör ett reproducerbart zoom-experiment i 'steps' steg mot targetX/targetY.
        // - Pausar animationen så mätningen inte störs.
        // - Sparar och återställer all mandelbrot-state (center, size, depth).
        // - Utför samma zoom-logik som animationstråden, men synkront.
        // - Summerar total tid (ms) för CPU ELLER GPU och returnerar (totalMs, steps).
        static (long totalMs, int steps) RunZoomExperiment(bool gpu, int steps, double targetX, double targetY)
        {
            // Pausa animationen så mätningen inte störs
            bool wasActive = activeMandelThread;
            activeMandelThread = false;

            // Spara ursprung
            double cx0 = mandelCenterX, cy0 = mandelCenterY, w0 = mandelWidth, h0 = mandelHeight;
            int depth0 = mandelDepth;

            long total = 0;
            for (int k = 0; k < steps; k++)
            {
                // samma zoom-logik som tråden
                mandelCenterX = (mandelCenterX * 0.8 + targetX * 0.2);
                mandelCenterY = (mandelCenterY * 0.8 + targetY * 0.2);
                mandelWidth *= 0.9;
                mandelHeight *= 0.9;
                mandelDepth += 2;

                var sw = Stopwatch.StartNew();
                if (gpu) UpdateMandelParallel(); else UpdateMandel();
                sw.Stop();
                total += sw.ElapsedMilliseconds;
            }

            // Återställ
            mandelCenterX = cx0; mandelCenterY = cy0; mandelWidth = w0; mandelHeight = h0; mandelDepth = depth0;

            // Återstarta ev. animation
            activeMandelThread = wasActive;

            return (total, steps);
        }

        /// <summary>
        /// This method implements the classic "Escape-Time Algorithm"
        /// for calulating the number of iterations necessary before
        /// a complex coordinate in the Mandelbrot rectangle "escapes"
        /// from a circle ("disk") of radius 2.
        /// </summary>
        /// <param name="cx">The center x coordinate in the complex plane.</param>
        /// <param name="cy">The center y coordinate in the complex plane.</param>
        /// <returns></returns>
        public static int IterCount(double cx, double cy)
        {
            int result = 0;

            double x = 0.0f;
            double y = 0.0f;
            double xx = 0.0f, yy = 0.0;

            while (xx + yy <= 4.0 && result < mandelDepth) // are we out of control disk?
            {
                xx = x * x;
                yy = y * y;
                double xtmp = xx - yy + cx;
                y = 2.0f * x * y + cy; // computes z^2 + c
                x = xtmp;
                result++;
            }

            return result;
        }

        /// <summary>
        /// This method converts a color from the Hue Saturation Value (HSV)
        /// color space to the Red Green Blue (RGB) color space.
        /// </summary>
        /// <param name="h">Hue.</param>
        /// <param name="S">Saturation.</param>
        /// <param name="V">value.</param>
        /// <param name="r">Red.</param>
        /// <param name="g">Green.</param>
        /// <param name="b">Blue.</param>
        static void HsvToRgb(double h, double S, double V, out int r, out int g, out int b)
        {
            double H = h;
            while (H < 0) { H += 360; }
                ;
            while (H >= 360) { H -= 360; }
                ;

            double R, G, B;

            if (V <= 0)
            {
                R = G = B = 0;
            }
            else if (S <= 0)
            {
                R = G = B = V;
            }
            else
            {
                double hf = H / 60.0;
                int i = (int)Math.Floor(hf);
                double f = hf - i;
                double pv = V * (1 - S);
                double qv = V * (1 - S * f);
                double tv = V * (1 - S * (1 - f));

                switch (i)
                {
                    // Red is the dominant color
                    case 0:
                        R = V;
                        G = tv;
                        B = pv;
                        break;

                    // Green is the dominant color
                    case 1:
                        R = qv;
                        G = V;
                        B = pv;
                        break;
                    case 2:
                        R = pv;
                        G = V;
                        B = tv;
                        break;

                    // Blue is the dominant color
                    case 3:
                        R = pv;
                        G = qv;
                        B = V;
                        break;
                    case 4:
                        R = tv;
                        G = pv;
                        B = V;
                        break;

                    // Red is the dominant color
                    case 5:
                        R = V;
                        G = pv;
                        B = qv;
                        break;

                    // Just in case we overshoot on our math by a little, we put these here.
                    // Since its a switch it won't slow us down at all to put these here.
                    case 6:
                        R = V;
                        G = tv;
                        B = pv;
                        break;
                    case -1:
                        R = V;
                        G = pv;
                        B = qv;
                        break;

                    // The color is not defined, we should throw an error.
                    default:
                        //LFATAL("i Value error in Pixel conversion, Value is %d", i);
                        R = G = B = V; // Just pretend its black/white
                        break;
                }
            }

            r = Clamp((int)(R * 255.0));
            g = Clamp((int)(G * 255.0));
            b = Clamp((int)(B * 255.0));
        }

        /// <summary>
        /// This method clamps (constrains) a value to 0-255.
        /// This is for 8-bit RGB color channels.
        /// </summary>
        static int Clamp(int i)
        {
            if (i < 0) return 0;
            if (i > 255) return 255;
            return i;
        }
    }
}

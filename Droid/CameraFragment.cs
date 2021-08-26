using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Android;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Core.Content;
using AndroidX.Fragment.App;
using Java.Lang;
using Java.Util.Concurrent;
using OpenCvSharp;
using Rg.Plugins.Popup.Services;
//using OpenCvSharp;
//using OpenCvSharp;
using Xamarin.Forms.Platform.Android;
using Point = OpenCvSharp.Point;

//using Java.IO;

namespace CustomRenderer.Droid
{

    public class ImageUtil
    {

        public static byte[] ToArray(System.IO.Stream s)
        {
            if (s == null)
                throw new ArgumentNullException(nameof(s));
            if (!s.CanRead)
                throw new ArgumentException("Stream cannot be read");

            System.IO.MemoryStream ms = s as System.IO.MemoryStream;
            if (ms != null)
                return ms.ToArray();

            long pos = s.CanSeek ? s.Position : 0L;
            if (pos != 0L)
                s.Seek(0, System.IO.SeekOrigin.Begin);

            byte[] result = new byte[s.Length];
            s.Read(result, 0, result.Length);
            if (s.CanSeek)
                s.Seek(pos, System.IO.SeekOrigin.Begin);
            return result;
        }

        public static byte[] NV21toJPEG(byte[] nv21, int width, int height, int quality)
        {
            System.IO.Stream fout = new System.IO.MemoryStream();
            YuvImage yuv = new YuvImage(nv21, ImageFormatType.Nv21, width, height, null);
            yuv.CompressToJpeg(new Android.Graphics.Rect(0, 0, width, height), quality, fout);
            return ToArray(fout);
        }

        // nv12: true = NV12, false = NV21
        public static byte[] YUV420toNV21(Image image)
        {
            
            Android.Graphics.Rect crop = image.CropRect;
            int width = crop.Width();
            int height = crop.Height();
            Image.Plane[] planes = image.GetPlanes();
            byte[] data = new byte[width * height * ImageFormat.GetBitsPerPixel(image.Format) / 8];
            byte[] rowData = new byte[planes[0].RowStride];

            int channelOffset = 0;
            int outputStride = 1;
            for (int i = 0; i < planes.Length; i++)
            {
                switch (i)
                {
                    case 0:
                        channelOffset = 0;
                        outputStride = 1;
                        break;
                    case 1:
                        channelOffset = width * height + 1;
                        outputStride = 2;
                        break;
                    case 2:
                        channelOffset = width * height;
                        outputStride = 2;
                        break;
                }

                Java.Nio.ByteBuffer buffer = planes[i].Buffer;
                int rowStride = planes[i].RowStride;
                int pixelStride = planes[i].PixelStride;

                int shift = (i == 0) ? 0 : 1;
                int w = width >> shift;
                int h = height >> shift;
                buffer.Position(rowStride * (crop.Top >> shift) + pixelStride * (crop.Left >> shift));
                for (int row = 0; row < h; row++)
                {
                    int length;
                    if (pixelStride == 1 && outputStride == 1)
                    {
                        length = w;
                        buffer.Get(data, channelOffset, length);
                        channelOffset += length;
                    }
                    else
                    {
                        length = (w - 1) * pixelStride + 1;
                        buffer.Get(rowData, 0, length);
                        for (int col = 0; col < w; col++)
                        {
                            data[channelOffset] = rowData[col * pixelStride];
                            channelOffset += outputStride;
                        }
                    }
                    if (row < h - 1)
                    {
                        buffer.Position(buffer.Position() + rowStride - length);
                    }
                }
            }
            return data;
        }

        
    }

    class CameraFragment : Fragment, TextureView.ISurfaceTextureListener
    {
        CameraDevice device;
        CaptureRequest.Builder sessionBuilder;
        CameraCaptureSession session;
        CameraTemplate cameraTemplate;
        CameraManager manager;
        ImageReader mImageReader;
        CaptureRequest.Builder mPreviewRequestBuilder;
        Handler mBackgroundHandler;
        HandlerThread mBackgroundThread;
        CameraCaptureCallback cameraCaptureCallback;

        bool cameraPermissionsGranted;
        bool busy;
        bool repeatingIsRunning;
        static bool checkFrame = false;
        int sensorOrientation;
        string cameraId;
        LensFacing cameraType;
        Scalar iav;


        Android.Util.Size previewSize;
        
        HandlerThread backgroundThread;
        Handler backgroundHandler = null;

        Java.Util.Concurrent.Semaphore captureSessionOpenCloseLock = new Java.Util.Concurrent.Semaphore(1);

        static AutoFitTextureView texture;

        TaskCompletionSource<CameraDevice> initTaskSource;
        TaskCompletionSource<bool> permissionsRequested;

        CameraManager Manager => manager ??= (CameraManager)Context.GetSystemService(Context.CameraService);
        static Button button1;
        static TextView tv1;
        static TextView[] tvs = new TextView[22];
        static GridView gv1;
        static FrameLayout frameLayout1;
        static ImageView imageView1;
        static ImageView imageView2;
        static int pst = 0;
        bool IsBusy
        {
            get => device == null || busy;
            set
            {
                busy = value;
            }
        }

        bool Available;

        public CameraPreview Element { get; set; }

        #region Constructors

        public CameraFragment()
        {
        }

        public CameraFragment(IntPtr javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        #endregion

        #region Overrides

        public override Android.Views.View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState) => inflater.Inflate(Resource.Layout.CameraFragment, null);
        //public override Android.Views.View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState) => inflater.Inflate(Resource.Layout.Greetings, null);
        public override void OnViewCreated(Android.Views.View view, Bundle savedInstanceState) {
            texture = view.FindViewById<AutoFitTextureView>(Resource.Id.cameratexture);
            button1 = view.FindViewById<Button>(Resource.Id.button1);
            //gv1 = view.FindViewById<GridView>(Resource.Id.gridView1);
            button1.Click += onButtonClicked;
            tv1 = view.FindViewById<TextView>(Resource.Id.textView1);
            frameLayout1 = view.FindViewById<FrameLayout>(Resource.Id.frameLayout1);
            imageView1 = view.FindViewById<ImageView>(Resource.Id.imageView1);
            imageView2 = view.FindViewById<ImageView>(Resource.Id.imageView2);
            imageView2.Visibility = ViewStates.Gone;
            //Rg.Plugins.Popup.Popup.Init();
            //showG();
            frameLayout1.Visibility = Android.Views.ViewStates.Gone;
            //gv1.Visibility = Android.Views.ViewStates.Gone;
            button1.Text = "Start";
           
            //imageView1.SetImageDrawable(Resource.Id.overla)
            tvs[0] = view.FindViewById<TextView>(Resource.Id.textView2);
            tvs[1] = view.FindViewById<TextView>(Resource.Id.textView3);
            tvs[2] = view.FindViewById<TextView>(Resource.Id.textView4);
            tvs[3] = view.FindViewById<TextView>(Resource.Id.textView5);
            tvs[4] = view.FindViewById<TextView>(Resource.Id.textView6);
            tvs[5] = view.FindViewById<TextView>(Resource.Id.textView7);
            tvs[6] = view.FindViewById<TextView>(Resource.Id.textView8);
            tvs[7] = view.FindViewById<TextView>(Resource.Id.textView9);
            tvs[8] = view.FindViewById<TextView>(Resource.Id.textView10);
            tvs[9] = view.FindViewById<TextView>(Resource.Id.textView11);
            tvs[10] = view.FindViewById<TextView>(Resource.Id.textView12);
            tvs[11] = view.FindViewById<TextView>(Resource.Id.textView13);
            tvs[12] = view.FindViewById<TextView>(Resource.Id.textView14);
            tvs[13] = view.FindViewById<TextView>(Resource.Id.textView15);
            tvs[14] = view.FindViewById<TextView>(Resource.Id.textView16);
            tvs[15] = view.FindViewById<TextView>(Resource.Id.textView17);
            tvs[16] = view.FindViewById<TextView>(Resource.Id.textView18);
            tvs[17] = view.FindViewById<TextView>(Resource.Id.textView19);
            tvs[18] = view.FindViewById<TextView>(Resource.Id.textView20);
            tvs[19] = view.FindViewById<TextView>(Resource.Id.textView21);
            tvs[20] = view.FindViewById<TextView>(Resource.Id.textView22);
            tvs[21] = view.FindViewById<TextView>(Resource.Id.textView23);

            for (int i = 0; i < 22; i++)
            {
                tvs[i].Visibility = Android.Views.ViewStates.Gone;
            }

        }
        
        
        async void showG()
        {
           
        }
        static public void scrupd(Scalar sc)
        {
            //tv1.Text = "TempColour" + (sc[0] + sc[1] + sc[2]) / 3;
        }
        private async void OnClickPopUpBtn(object sender, EventArgs e)
        {
            
           
        }

        public override void OnPause()
        {
            CloseSession();
            StopBackgroundThread();
            base.OnPause();            
        }
        
        async void onButtonClicked(object sender, EventArgs args)

        {
            if (pst==1)
            {
                checkFrame = true;
                tv1.Text = "Searching...";
            }
            if (pst==0)
            {
                imageView2.Visibility = ViewStates.Gone;
                frameLayout1.Visibility = Android.Views.ViewStates.Visible;
                button1.Text = "Scan";
                tv1.Text="Press Scan to start scan";
                pst = 1;
            }
            
        }

        public override async void OnResume()
        {
            
            base.OnResume();

            StartBackgroundThread();
            if (texture is null)
            {
                return;
            }
            if (texture.IsAvailable)
            {
                View?.SetBackgroundColor(Element.BackgroundColor.ToAndroid());
                cameraTemplate = CameraTemplate.Preview;
                await RetrieveCameraDevice(force: true);
            }
            else
            {
                texture.SurfaceTextureListener = this;
            }
        }

        protected override void Dispose(bool disposing)
        {
            CloseDevice();
            base.Dispose(disposing);
        }

        #endregion

        #region Public methods

        public async Task RetrieveCameraDevice(bool force = false)
        {
            if (Context == null || (!force && initTaskSource != null))
            {
                return;
            }

            if (device != null)
            {
                CloseDevice();
            }

            await RequestCameraPermissions();
            if (!cameraPermissionsGranted)
            {
                return;
            }

            if (!captureSessionOpenCloseLock.TryAcquire(2500, TimeUnit.Milliseconds))
            {
                throw new RuntimeException("Timeout waiting to lock camera opening.");
            }

            IsBusy = true;
            cameraId = GetCameraId();

            if (string.IsNullOrEmpty(cameraId))
            {
                IsBusy = false;
                captureSessionOpenCloseLock.Release();
                Console.WriteLine("No camera found");
            }
            else
            {
                try
                {
                    CameraCharacteristics characteristics = Manager.GetCameraCharacteristics(cameraId);
                    StreamConfigurationMap map = (StreamConfigurationMap)characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap);

                    previewSize = ChooseOptimalSize(map.GetOutputSizes(Class.FromType(typeof(SurfaceTexture))),
                        texture.Width, texture.Height, GetMaxSize(map.GetOutputSizes((int)ImageFormatType.Jpeg)));
                    sensorOrientation = (int)characteristics.Get(CameraCharacteristics.SensorOrientation);
                    cameraType = (LensFacing)(int)characteristics.Get(CameraCharacteristics.LensFacing);

                    if (Resources.Configuration.Orientation == Android.Content.Res.Orientation.Landscape)
                    {
                        texture.SetAspectRatio(previewSize.Width, previewSize.Height);
                    }
                    else
                    {
                        texture.SetAspectRatio(previewSize.Height, previewSize.Width);
                    }

                    initTaskSource = new TaskCompletionSource<CameraDevice>();
                    Manager.OpenCamera(cameraId, new CameraStateListener
                    {
                        OnOpenedAction = device => initTaskSource?.TrySetResult(device),
                        OnDisconnectedAction = device =>
                        {
                            initTaskSource?.TrySetResult(null);
                            CloseDevice(device);
                        },
                        OnErrorAction = (device, error) =>
                        {
                            initTaskSource?.TrySetResult(device);
                            Console.WriteLine($"Camera device error: {error}");
                            CloseDevice(device);
                        },
                        OnClosedAction = device =>
                        {
                            initTaskSource?.TrySetResult(null);
                            CloseDevice(device);
                        }
                    }, backgroundHandler);

                    captureSessionOpenCloseLock.Release();
                    device = await initTaskSource.Task;
                    initTaskSource = null;
                    if (device != null)
                    {
                        await PrepareSession();
                    }
                }
                catch (Java.Lang.Exception ex)
                {
                    Console.WriteLine("Failed to open camera.", ex);
                    Available = false;
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }

        public void UpdateRepeatingRequest()
        {
            try
            {
                if (session == null || sessionBuilder == null)
            {
                return;
            }

            IsBusy = true;
            
                if (repeatingIsRunning)
                {
                    session.StopRepeating();
                }

                sessionBuilder.Set(CaptureRequest.ControlMode, (int)ControlMode.Auto);
                sessionBuilder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.On);
                session.SetRepeatingRequest(sessionBuilder.Build(), listener: null, backgroundHandler);
                repeatingIsRunning = true;
            
            }
            catch (System.Exception error)
            {
                Console.WriteLine("Update preview exception.", error.Message);
            }
            
            finally
            {
                IsBusy = false;
            }
        }

        #endregion

        void StartBackgroundThread()
        {
            backgroundThread = new HandlerThread("CameraBackground");
            backgroundThread.Start();
            backgroundHandler = new Handler(backgroundThread.Looper);
        }

        void StopBackgroundThread()
        {
            if (backgroundThread == null)
            {
                return;
            }

            backgroundThread.QuitSafely();
            try
            {
                backgroundThread.Join();
                backgroundThread = null;
                backgroundHandler = null;
            }
            catch (InterruptedException ex)
            {
                Console.WriteLine("Error stopping background thread.", ex);
            }
        }        

        Android.Util.Size GetMaxSize(Android.Util.Size[] imageSizes)
        {
            Android.Util.Size maxSize = null;
            long maxPixels = 0;
            for (int i = 0; i < imageSizes.Length; i++)
            {
                long currentPixels = imageSizes[i].Width * imageSizes[i].Height;
                if (currentPixels > maxPixels)
                {
                    maxSize = imageSizes[i];
                    maxPixels = currentPixels;
                }
            }
            return maxSize;
        }

        Android.Util.Size ChooseOptimalSize(Android.Util.Size[] choices, int width, int height, Android.Util.Size aspectRatio)
        {
            List<Android.Util.Size> bigEnough = new List<Android.Util.Size>();
            int w = aspectRatio.Width;
            int h = aspectRatio.Height;

            foreach (Android.Util.Size option in choices)
            {
                if (option.Height == option.Width * h / w && option.Width >= width && option.Height >= height)
                {
                    bigEnough.Add(option);
                }
            }

            if (bigEnough.Count > 0)
            {
                int minArea = bigEnough.Min(s => s.Width * s.Height);
                return bigEnough.First(s => s.Width * s.Height == minArea);
            }
            else
            {
                Console.WriteLine("Couldn't find any suitable preview size.");
                return choices[0];
            }
        }

        string GetCameraId()
        {
            string[] cameraIdList = Manager.GetCameraIdList();
            if (cameraIdList.Length == 0)
            {
                return null;
            }

            string FilterCameraByLens(LensFacing lensFacing)
            {
                foreach (string id in cameraIdList)
                {
                    CameraCharacteristics characteristics = Manager.GetCameraCharacteristics(id);
                    if (lensFacing == (LensFacing)(int)characteristics.Get(CameraCharacteristics.LensFacing))
                    {
                        return id;
                    }
                }
                return null;
            }

            return (Element.Camera == CameraOptions.Front) ? FilterCameraByLens(LensFacing.Front) : FilterCameraByLens(LensFacing.Back);
        }

        private void ProcessImageCapture(CaptureResult result)
        {
            Console.WriteLine("ProcessImageCapture input");
        }

        public void OnPictureTaken(byte[] data, Camera camera)
        {


            Java.IO.FileOutputStream outStream = null;
            Java.IO.File dataDir = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDcim);
            if (data != null)
            {
                try
                {
                    TimeSpan ts = DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, 0);
                    var s = ts.TotalMilliseconds;
                    outStream = new Java.IO.FileOutputStream(dataDir + "/" + s + ".jpg");
                    outStream.Write(data);
                    outStream.Close();
                }
                catch (Java.IO.FileNotFoundException e)
                {
                    System.Console.Out.WriteLine(e.Message);
                }
                catch (Java.IO.IOException ie)
                {
                    System.Console.Out.WriteLine(ie.Message);
                }
            }
            //camera.StartPreview();
        }

        private static void rotateImage(Mat src, Mat dst, double angle, double scale)
        {
            var imageCenter = new Point2f(src.Cols / 2f, src.Rows / 2f);
            var rotationMat = Cv2.GetRotationMatrix2D(imageCenter, angle, scale);
            Cv2.WarpAffine(src, dst, rotationMat, src.Size());
        }




        public class MyImageReaderListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
        {
            public void Dispose()
            {
                //TODO:
            }



            public void OnImageAvailable(ImageReader reader)
            {


                var image = reader.AcquireNextImage();

                try
                {

               
                void ExportBitmapAsPNG(Bitmap bitmap, string fname)
                {
                    var sdCardPath = Android.OS.Environment.ExternalStorageDirectory.AbsolutePath;
                    var filePath = System.IO.Path.Combine(sdCardPath+"/DCIM/CScanner", fname);

                    if (!System.IO.Directory.Exists(sdCardPath + "/DCIM/CScanner")){
                        System.IO.Directory.CreateDirectory(sdCardPath + "/DCIM/CScanner");
                    }

                    var stream = new System.IO.FileStream(filePath, System.IO.FileMode.Create);
                    bitmap.Compress(Bitmap.CompressFormat.Png, 100, stream);
                    stream.Close();
                }



                if (checkFrame)
                {

                    //YuvImage 

                    byte[] nv21 = ImageUtil.YUV420toNV21(image);
                    byte[] data = ImageUtil.NV21toJPEG(nv21, image.Width, image.Height, 100);

                    Mat img = Cv2.ImDecode(data, ImreadModes.AnyColor);
                    Cv2.Transpose(img, img);
                    Cv2.Flip(img, img, OpenCvSharp.FlipMode.Y);

                    Mat irth = img.Clone();
                    Mat display = img.Clone();
                    Cv2.CvtColor(irth, irth, ColorConversionCodes.BGR2GRAY);
                    Cv2.Threshold(irth, irth, 120, 255, ThresholdTypes.BinaryInv);

                    double psq = 0;
                    double pw = 0;
                    OpenCvSharp.Point bcenter = new OpenCvSharp.Point(0, 0);

                    bool fnd = false;                   

                    OpenCvSharp.Point[][] cntrs = Cv2.FindContoursAsArray(irth, RetrievalModes.List, ContourApproximationModes.ApproxSimple);

                    foreach (var cont in cntrs)
                    {
                        if ((Cv2.ContourArea(cont) > 3000) && Cv2.ContourArea(cont) < 850000)
                        {
                            //Console.WriteLine("Found point {0}" , Cv2.ContourArea(cont));
                            Size s = irth.Size();
                            OpenCvSharp.Rect line_rect = Cv2.BoundingRect(cont);
                            //cout << "New contour " << Cv2.contourArea(cont) << " x: " << line_rect.x << " y: " << line_rect.y << " width: " << line_rect.width << " height: " << line_rect.height << "\n";
                            if ((s.Width / line_rect.Width > 2 && s.Width / line_rect.Width < 10) && (s.Width / line_rect.Height > 2 && s.Width / line_rect.Height < 10))
                            {
                                //cout << "New contour " << Cv2.contourArea(cont) << " x: " << line_rect.x << " y: " << line_rect.y << " width: " << line_rect.width << " height: " << line_rect.height << "\n";
                                double kw = (double)s.Width / (double)line_rect.Width;
                                double kh = (double)line_rect.Width / (double)line_rect.Height;
                                if (kw > 3.6 && kw < 4.2 && kh > 0.9 && kh < 1.1)
                                {
                                    Console.WriteLine("Found point");
                                    fnd = true;
                                    psq = Cv2.ContourArea(cont);
                                    pw = line_rect.Width;
                                    bcenter = (line_rect.BottomRight + line_rect.TopLeft) * 0.5;

                                }
                            }
                            //cfil.push_back(cont);
                        }
                    }

                    if (fnd)
                    {
                       

                        frameLayout1.Visibility = ViewStates.Gone;
                        frameLayout1.RequestLayout();
                        //< Label Text = "Camera Preview:" />
                        imageView2.Visibility = ViewStates.Visible;

                        tv1.Text = "Found pattern";
                        button1.Text = "Press to scan again";
                        pst = 0;

                        OpenCvSharp.Point[] coord = new OpenCvSharp.Point[23];

                        coord[0] = new Point(bcenter.X + pw * 0.975, bcenter.Y - pw * 0.325);
                        Cv2.Circle(display, coord[0].X, coord[0].Y, (int)(pw / 6), new Scalar(240, 0, 240), 3);

                        for (int i = 1; i < 3; i++)
                        {
                            coord[i] = new Point(coord[i - 1].X + pw * 0.66, coord[i - 1].Y);
                            Cv2.Circle(display, coord[i].X,coord[i].Y, (int)(pw / 6), new Scalar(240, 0, 240), 3);

                        }

                        // second row		
                        for (int i = 3; i < 6; i++)
                        {
                            coord[i] = new Point(coord[i - 3].X, coord[i - 3].Y + pw * 0.66);
                            Cv2.Circle(display, coord[i].X, coord[i].Y, (int)(pw / 6), new Scalar(240, 0, 240), 3);
                        }

                        // third row	

                        coord[6] = new Point(bcenter.X - pw * 0.33, coord[5].Y + pw * 0.66);
                        Cv2.Circle(display, coord[6].X, coord[6].Y, (int)(pw / 6), new Scalar(240, 0, 240), 3);


                        for (int i = 7; i < 11; i++)
                        {
                            coord[i] = new Point(coord[i - 1].X + pw * 0.66, coord[i - 1].Y);
                            Cv2.Circle(display, coord[i].X, coord[i].Y, (int)(pw / 6), new Scalar(240, 0, 240),3);
                        }

                        //fourth row
                        for (int i = 11; i < 21; i++)
                        {
                            coord[i] = new Point(coord[i - 5].X, coord[i - 5].Y + pw * 0.66);
                            Cv2.Circle(display, coord[i].X, coord[i].Y, (int)(pw / 6), new Scalar(240, 0, 240), 3);
                        }
                        System.String[] cameraNames = { "1.3", "1.4", "1.5", "2.3", "2.4", "2.5", "3.1", "3.2", "3.3", "3.4", "3.5", "4.1", "4.2", "4.3", "4.4", "4.5", "5.1", "5.2", "5.3", "5.4", "5.5", "C" };

                        coord[21] = bcenter;
                        Cv2.Circle(display, coord[21].X, coord[21].Y, (int)(pw / 6), new Scalar(240, 0, 240), 3);

                        OpenCvSharp.Point[][] cnc = new OpenCvSharp.Point[22][];

                        OpenCvSharp.Point[] cnl = new OpenCvSharp.Point[4];
                        cnl[0] = (new Point(coord[21].X - pw / 20, coord[21].Y - pw / 20));
                        cnl[1] = (new Point(coord[21].X - pw / 20, coord[21].Y + pw / 20));
                        cnl[2] = (new Point(coord[21].X + pw / 20, coord[21].Y + pw / 20));
                        cnl[3] = (new Point(coord[21].X + pw / 20, coord[21].Y - pw / 20));
                        cnc[21] = cnl;
                        OpenCvSharp.Rect _boundingRectL = Cv2.BoundingRect(cnl);
                        Scalar mean_color0L = Cv2.Mean(img[_boundingRectL]);
                        Console.WriteLine("Colour {0} is {1}", 21, mean_color0L.ToString());
                        tvs[21].Visibility = Android.Views.ViewStates.Visible;

                        System.String colorStringL = ("#" + Convert.ToInt32(mean_color0L.Val2).ToString("X2") + Convert.ToInt32(mean_color0L.Val1).ToString("X2") + Convert.ToInt32(mean_color0L.Val0).ToString("X2"));
                        tvs[21].Text = "Colour №" + cameraNames[21] + " is " + colorStringL;
                        Console.WriteLine(colorStringL);

                        string writeline = "Colour №" + cameraNames[21] + " is " + colorStringL + "\n";

                        try
                        {
                            tvs[21].SetTextColor(Color.ParseColor(colorStringL));

                        }
                        catch (System.Exception ex)
                        {

                            Console.WriteLine(ex.Message);
                        }

                        Color diffColor = Color.White;
                        Color parseColor = Color.ParseColor(colorStringL);
                        
                        diffColor.B = (byte)((byte)255 -parseColor.B);
                        diffColor.G = (byte)((byte)255 - parseColor.G);
                        diffColor.R = (byte)((byte)255 - parseColor.R);

                        byte[] irb = display.ToBytes();
                        Bitmap bitmapImage = BitmapFactory.DecodeByteArray(irb, 0, irb.Length, null);
                        imageView2.SetImageBitmap(bitmapImage);
                        string filename = DateTime.Now.ToString("dd-MM-YYYY HH:mm:ss") ;                     
                        
                        ExportBitmapAsPNG(bitmapImage, filename + ".png");
          
                   
                        


                        for (int i = 0; i < 22; i++)
                        {

                            OpenCvSharp.Point[] cn = new OpenCvSharp.Point[4];
                            cn[0] = (new Point(coord[i].X - pw / 20, coord[i].Y - pw / 20));
                            cn[1] = (new Point(coord[i].X - pw / 20, coord[i].Y + pw / 20));
                            cn[2] = (new Point(coord[i].X + pw / 20, coord[i].Y + pw / 20));
                            cn[3] = (new Point(coord[i].X + pw / 20, coord[i].Y - pw / 20));
                            cnc[i] = cn;
                            OpenCvSharp.Rect _boundingRect = Cv2.BoundingRect(cn);
                            Scalar mean_color0 = Cv2.Mean(img[_boundingRect]);
                            Console.WriteLine("Colour {0} is {1}", i ,mean_color0.ToString());
                            tvs[i].Visibility = Android.Views.ViewStates.Visible;
                            

                            System.String colorString = ("#" + Convert.ToInt32(mean_color0.Val2).ToString("X2") + Convert.ToInt32(mean_color0.Val1).ToString("X2") + Convert.ToInt32(mean_color0.Val0).ToString("X2"));
                            Color tColor = Color.ParseColor(colorString);
                            try
                            {
                               
                               // tColor.B = (byte)(tColor.B + diffColor.B);
                               // tColor.G = (byte)(tColor.G + diffColor.G);
                               // tColor.R = (byte)(tColor.R + diffColor.R);
                                tvs[i].SetTextColor(tColor);

                            }
                            catch (System.Exception ex)
                            {

                                Console.WriteLine(ex.Message);
                            }

                            tvs[i].Text = "Colour №" + cameraNames[i] + " is " + tColor.ToString();
                            writeline += "Colour №" + cameraNames[i] + " is " + tColor.ToString() +" "+ colorString + " \n";
                            Console.WriteLine(colorString);

                        }
                        var sdCardPath = Android.OS.Environment.ExternalStorageDirectory.AbsolutePath;
                        var filePath = System.IO.Path.Combine(sdCardPath + "/DCIM/CScanner",filename);
                        System.IO.File.WriteAllText(filePath+".txt", writeline);
                    }else
                    {
                        tv1.Text = "Pattern not found";
                    }
                
            

                    Console.WriteLine(img.Rows);
                    Console.WriteLine("Frame!");                        
                        checkFrame = false;
                   
                    }
                            
                }
                catch (System.Exception e)
                {

                    tv1.Text = e.Message;
                }
                image.Close();
            }
         
        }

       
        async Task PrepareSession()
        {
            IsBusy = true;
            try
            {
                CloseSession();
                sessionBuilder = device.CreateCaptureRequest(cameraTemplate);
                if (mImageReader == null)
                {
                    mImageReader = ImageReader.NewInstance(previewSize.Width/4, previewSize.Height/4,
                                        Android.Graphics.ImageFormatType.Yuv420888, 4);
                    mImageReader.SetOnImageAvailableListener(new MyImageReaderListener(), null);
                }
                List<Surface> surfaces = new List<Surface>();
                if (texture.IsAvailable && previewSize != null)
                {
                    var texture = CameraFragment.texture.SurfaceTexture;
                    
                    texture.SetDefaultBufferSize(previewSize.Width, previewSize.Height);
                    Surface previewSurface = new Surface(texture);
                    surfaces.Add(previewSurface);
                    surfaces.Add(mImageReader.Surface);
                    sessionBuilder.AddTarget(previewSurface);                    
                    sessionBuilder.AddTarget(mImageReader.Surface);
                    
                }



                TaskCompletionSource<CameraCaptureSession> tcs = new TaskCompletionSource<CameraCaptureSession>();
                device.CreateCaptureSession(surfaces, new CameraCaptureStateListener
                {
                    OnConfigureFailedAction = captureSession =>
                    {
                        tcs.SetResult(null);
                        Console.WriteLine("Failed to create capture session.");
                    },
                    OnConfiguredAction = captureSession => tcs.SetResult(captureSession)
                }, null);

                session = await tcs.Task;
                if (session != null)
                {
                    UpdateRepeatingRequest();
                }
            }
            catch (Java.Lang.Exception ex)
            {
                Available = false;
                Console.WriteLine("Capture error.", ex.Message);
            }
            finally
            {
                Available = session != null;
                IsBusy = false;
            }
        }



        void CloseSession()
        {
            repeatingIsRunning = false;
            if (session == null)
            {
                return;
            }

            try
            {
                session.StopRepeating();
                session.AbortCaptures();
                session.Close();
                session.Dispose();
                session = null;
            }
            catch (CameraAccessException ex)
            {
                Console.WriteLine("Camera access error.", ex);
            }
            catch (Java.Lang.Exception ex)
            {
                Console.WriteLine("Error closing device.", ex);
            }
        }

        void CloseDevice(CameraDevice inputDevice)
        {
            if (inputDevice == device)
            {
                CloseDevice();
            }
        }

        void CloseDevice()
        {
            CloseSession();

            try
            {
                if (sessionBuilder != null)
                {
                    sessionBuilder.Dispose();
                    sessionBuilder = null;
                }
                if (device != null)
                {
                    device.Close();
                    device = null;
                }
            }
            catch (Java.Lang.Exception error)
            {
                Console.WriteLine("Error closing device.", error);
            }
        }

        void ConfigureTransform(int viewWidth, int viewHeight)
        {
            if (texture == null || previewSize == null || previewSize.Width == 0 || previewSize.Height == 0)
            {
                return;
            }

            var matrix = new Matrix();
            var viewRect = new RectF(0, 0, viewWidth, viewHeight);
            var bufferRect = new RectF(0, 0, previewSize.Height, previewSize.Width);
            var centerX = viewRect.CenterX();
            var centerY = viewRect.CenterY();
            bufferRect.Offset(centerX - bufferRect.CenterX(), centerY - bufferRect.CenterY());
            matrix.SetRectToRect(viewRect, bufferRect, Matrix.ScaleToFit.Fill);
            matrix.PostRotate(GetCaptureOrientation(), centerX, centerY);
            texture.SetTransform(matrix);
        }

        int GetCaptureOrientation()
        {
            int frontOffset = cameraType == LensFacing.Front ? 90 : -90;
            return (360 + sensorOrientation - GetDisplayRotationDegrees() + frontOffset) % 360;
        }

        int GetDisplayRotationDegrees() =>
            GetDisplayRotation() switch
            {
                SurfaceOrientation.Rotation90 => 90,
                SurfaceOrientation.Rotation180 => 180,
                SurfaceOrientation.Rotation270 => 270,
                _ => 0
            };

        SurfaceOrientation GetDisplayRotation() => Android.App.Application.Context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>().DefaultDisplay.Rotation;

        #region Permissions

        async Task RequestCameraPermissions()
        {
            if (permissionsRequested != null)
            {
                await permissionsRequested.Task;
            }

            List<string> permissionsToRequest = new List<string>();
            cameraPermissionsGranted = ContextCompat.CheckSelfPermission(Context, Manifest.Permission.Camera) == Permission.Granted;
            if (!cameraPermissionsGranted)
            {
                permissionsToRequest.Add(Manifest.Permission.Camera);
            }

            cameraPermissionsGranted = ContextCompat.CheckSelfPermission(Context, Manifest.Permission.ReadExternalStorage) == Permission.Granted;
            if (!cameraPermissionsGranted)
            {
                permissionsToRequest.Add(Manifest.Permission.ReadExternalStorage);
            }

            cameraPermissionsGranted = ContextCompat.CheckSelfPermission(Context, Manifest.Permission.WriteExternalStorage) == Permission.Granted;
            if (!cameraPermissionsGranted)
            {
                permissionsToRequest.Add(Manifest.Permission.WriteExternalStorage);
            }

            if (permissionsToRequest.Count > 0)
            {
                permissionsRequested = new TaskCompletionSource<bool>();
                RequestPermissions(permissionsToRequest.ToArray(), requestCode: 1);
                await permissionsRequested.Task;
                permissionsRequested = null;
            }
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Permission[] grantResults)
        {
            if (requestCode != 1)
            {
                base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
                return;
            }

            for (int i=0; i < permissions.Length; i++)
            {
                if (permissions[i] == Manifest.Permission.Camera)
                {
                    cameraPermissionsGranted = grantResults[i] == Permission.Granted;
                    if (!cameraPermissionsGranted)
                    {
                        Console.WriteLine("No permission to use the camera.");
                    }
                }
            }
            permissionsRequested?.TrySetResult(true);
        }

        #endregion

        #region TextureView.ISurfaceTextureListener

        async void TextureView.ISurfaceTextureListener.OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
        {
            View?.SetBackgroundColor(Element.BackgroundColor.ToAndroid());
            cameraTemplate = CameraTemplate.Preview;
            await RetrieveCameraDevice();           
        }

        bool TextureView.ISurfaceTextureListener.OnSurfaceTextureDestroyed(SurfaceTexture surface)
        {
            CloseDevice();
            return true;
        }

        void TextureView.ISurfaceTextureListener.OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) => ConfigureTransform(width, height);

        void TextureView.ISurfaceTextureListener.OnSurfaceTextureUpdated(SurfaceTexture surface)
        {
            
        }

        public class CameraCaptureCallback : CameraCaptureSession.CaptureCallback
        {
            public Action<CameraCaptureSession, CaptureRequest, TotalCaptureResult> CaptureCompleted;

            public Action<CameraCaptureSession, CaptureRequest, CaptureResult> CaptureProgressed;

            public override void OnCaptureCompleted(CameraCaptureSession session, CaptureRequest request, TotalCaptureResult result)
            {
                CaptureCompleted?.Invoke(session, request, result);
            }

            public override void OnCaptureProgressed(CameraCaptureSession session, CaptureRequest request, CaptureResult partialResult)
            {
                CaptureProgressed?.Invoke(session, request, partialResult);
            }
        }
        #endregion
    }
}
